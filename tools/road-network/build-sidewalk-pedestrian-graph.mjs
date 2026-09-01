#!/usr/bin/env node

import { createHash } from 'node:crypto'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { geographicToPlane, planeToGeographic } from '../environment-cost-network/japan-plane-rectangular.mjs'

const EARTH = 6371008.8
const EXCLUDED = new Set(['motorway', 'motorway_link', 'trunk', 'trunk_link', 'construction', 'proposed', 'raceway'])
const PEDESTRIAN = new Set(['footway', 'path', 'pedestrian', 'steps', 'crossing'])
const SIDES = { left: ['left'], right: ['right'], both: ['left', 'right'] }

function usage() { return 'Usage: node tools/road-network/build-sidewalk-pedestrian-graph.mjs --config <config.json> --osm <capture-0.2.json> --output <graph.json> --report <report.json>' }
export function parseArgs(args) { const o = {}; for (let i = 0; i < args.length; i += 2) { if (!args[i]?.startsWith('--') || !args[i + 1]) throw new Error(usage()); o[args[i].slice(2)] = args[i + 1] } for (const k of ['config', 'osm', 'output', 'report']) if (!o[k]) throw new Error(`--${k} is required`); return o }
function meters(a, b) { const r = Math.PI / 180; const dlat = (b[1] - a[1]) * r; const dlon = (b[0] - a[0]) * r; const h = Math.sin(dlat / 2) ** 2 + Math.cos(a[1] * r) * Math.cos(b[1] * r) * Math.sin(dlon / 2) ** 2; return 2 * EARTH * Math.asin(Math.sqrt(h)) }
function coordinate(point) { const result = [point?.lon, point?.lat]; if (!result.every(Number.isFinite)) throw new Error('invalid geometry coordinate'); return result }
function level(tags = {}) { const n = Number(tags.level ?? tags.layer ?? 0); return Number.isFinite(n) ? n : 0 }
function walkable(tags = {}) { return !EXCLUDED.has(tags.highway) && tags.area !== 'yes' && tags.foot !== 'no' && !((tags.access === 'no' || tags.access === 'private') && !['yes', 'designated'].includes(tags.foot)) }
function offset(a, b, side, zone, width = 2) { const pa = geographicToPlane(a, zone), pb = geographicToPlane(b, zone), dx = pb.eastingMeters - pa.eastingMeters, dy = pb.northingMeters - pa.northingMeters, length = Math.hypot(dx, dy); if (length < .01) return a; const sign = side === 'left' ? 1 : -1; return planeToGeographic({ eastingMeters: pa.eastingMeters + sign * -dy / length * width, northingMeters: pa.northingMeters + sign * dx / length * width }, zone) }
function sourceFor(kind, id, rule, fallback) { return { kind, id: String(id), rule, confidence: fallback ? 'fallback' : rule.startsWith('highway=') ? 'explicit' : 'derived' } }
function stable(value) { return JSON.stringify(value) }

export function buildGraph(config, osm) {
  if (!Array.isArray(osm?.elements)) throw new Error('OSM input does not contain elements')
  const types = new Set(osm.elements.map((e) => e?.type)); if (osm.captureContractVersion !== '0.2' || !types.has('way') || !types.has('node')) throw new Error('capture-contract-0.2 required: way, node, and relation-query marker; way-only snapshot is not accepted')
  const nodeTags = new Map(osm.elements.filter((e) => e.type === 'node').map((e) => [e.id, e.tags ?? {}]))
  const nodes = new Map(), edges = [], physicalEdges = [], diagnostics = { sourceWayCount: 0, excludedWayCount: 0, explicitWayCount: 0, derivedSidewalkWayCount: 0, separateSidewalkSkippedCount: 0, fallbackWayCount: 0, crossingConnectionCount: 0, malformedWayCount: 0 }
  const addNode = (id, coord, rawId, side, z, kind, source) => { if (!nodes.has(id)) nodes.set(id, { id, coordinate: coord, zLevel: z, kind, source, rawOsmNodeId: rawId, side }); return id }
  const addBidirectional = (base, from, to, geometry, detail) => {
    const length = meters(geometry[0], geometry[1])
    if (length <= .01 || from === to) return
    const physicalEdge = { id: base, fromNodeId: from, toNodeId: to, geometry, lengthMeters: length, walkingSeconds: length / 1.4, walkability: 'walkable', ...detail }
    physicalEdges.push(physicalEdge)
    for (const [suffix, a, b] of [['forward', from, to], ['backward', to, from]]) edges.push({ id: `${base}:${suffix}`, physicalEdgeId: base, fromNodeId: a, toNodeId: b, walkingSeconds: physicalEdge.walkingSeconds, walkability: 'walkable', facility: detail.facility, side: detail.side, level: detail.level, source: detail.source, fallback: detail.fallback })
  }
  const ways = osm.elements.filter((e) => e.type === 'way').sort((a, b) => a.id - b.id)
  for (const way of ways) {
    diagnostics.sourceWayCount++; const tags = way.tags ?? {}, highway = tags.highway
    if (!walkable(tags) || !highway) { diagnostics.excludedWayCount++; continue }
    if (!Array.isArray(way.nodes) || !Array.isArray(way.geometry) || way.nodes.length !== way.geometry.length || way.nodes.length < 2) { diagnostics.malformedWayCount++; continue }
    const explicit = PEDESTRIAN.has(highway), sidewalk = tags.sidewalk
    if (!explicit && sidewalk === 'separate') { diagnostics.separateSidewalkSkippedCount++; continue }
    const variants = explicit ? [{ side: 'none', facility: highway, rule: `highway=${highway}`, fallback: false }] : (SIDES[sidewalk]?.map((side) => ({ side, facility: 'sidewalk', rule: `sidewalk=${sidewalk}`, fallback: false })) ?? [{ side: 'center', facility: 'centerline', rule: 'v1-centerline-fallback', fallback: true }])
    if (explicit) diagnostics.explicitWayCount++; else if (variants[0].fallback) diagnostics.fallbackWayCount++; else diagnostics.derivedSidewalkWayCount++
    for (const variant of variants) for (let i = 0; i < way.nodes.length - 1; i++) {
      let a, b; try { a = coordinate(way.geometry[i]); b = coordinate(way.geometry[i + 1]) } catch { diagnostics.malformedWayCount++; continue }
      const z = level(tags), aa = variant.side === 'left' || variant.side === 'right' ? offset(a, b, variant.side, config.coordinateZoneId) : a
      const bb = (() => { if (variant.side !== 'left' && variant.side !== 'right') return b; const pa = geographicToPlane(a, config.coordinateZoneId), pb = geographicToPlane(b, config.coordinateZoneId), shifted = geographicToPlane(aa, config.coordinateZoneId); return planeToGeographic({ eastingMeters: pb.eastingMeters + shifted.eastingMeters - pa.eastingMeters, northingMeters: pb.northingMeters + shifted.northingMeters - pa.northingMeters }, config.coordinateZoneId) })()
      const from = addNode(`ped:osm-node:${way.nodes[i]}:${variant.side}:l${z}`, aa, way.nodes[i], variant.side, z, variant.facility === 'centerline' ? 'fallback-junction' : 'sidewalk-junction', sourceFor(variant.fallback ? 'v1-centerline' : 'osm-node', way.nodes[i], variant.rule, variant.fallback))
      const to = addNode(`ped:osm-node:${way.nodes[i + 1]}:${variant.side}:l${z}`, bb, way.nodes[i + 1], variant.side, z, variant.facility === 'centerline' ? 'fallback-junction' : 'sidewalk-junction', sourceFor(variant.fallback ? 'v1-centerline' : 'osm-node', way.nodes[i + 1], variant.rule, variant.fallback))
      addBidirectional(`ped:way:${way.id}:${variant.side}:${i}`, from, to, [aa, bb], { facility: variant.facility, side: variant.side, level: z, crossing: null, source: sourceFor(variant.fallback ? 'v1-centerline' : 'osm-way', way.id, variant.rule, variant.fallback), fallback: variant.fallback })
    }
  }
  // A tagged crossing node may connect different side variants, but never different levels.
  const groups = new Map(); for (const node of nodes.values()) { const key = `${node.rawOsmNodeId}|${node.zLevel}`; const list = groups.get(key) ?? []; list.push(node); groups.set(key, list) }
  for (const [key, group] of groups) { const [raw] = key.split('|'); const tags = nodeTags.get(Number(raw)) ?? {}; if (!('crossing' in tags || tags.highway === 'crossing')) continue; group.sort((a, b) => a.id.localeCompare(b.id)); for (let i = 1; i < group.length; i++) { const a = group[0], b = group[i]; if (a.side === b.side) continue; addBidirectional(`ped:crossing:${raw}:l${a.zLevel}:${i}`, a.id, b.id, [a.coordinate, b.coordinate], { facility: 'crossing', side: 'none', level: a.zLevel, crossing: String(tags.crossing ?? 'yes'), source: sourceFor('osm-node', raw, 'crossing-tag', false), fallback: false }); diagnostics.crossingConnectionCount++ } }
  const output = { nodes: [...nodes.values()].sort((a, b) => a.id.localeCompare(b.id)), physicalEdges: physicalEdges.sort((a, b) => a.id.localeCompare(b.id)), edges: edges.sort((a, b) => a.id.localeCompare(b.id)), diagnostics }
  return output
}
export function graphFingerprint(graph) { return createHash('sha256').update(stable({ nodes: graph.nodes.map((n) => [n.id, n.coordinate, n.zLevel, n.side]), physicalEdges: graph.physicalEdges.map((e) => [e.id, e.fromNodeId, e.toNodeId, e.geometry, e.lengthMeters, e.walkingSeconds, e.facility, e.side, e.level, e.fallback]), edges: graph.edges.map((e) => [e.id, e.physicalEdgeId, e.fromNodeId, e.toNodeId, e.walkingSeconds]) })).digest('hex') }
function representativeOdResults(graph, definitions = []) {
  if (!Array.isArray(definitions) || definitions.length === 0) return { status: 'blocked', reason: 'representative-od-not-configured', routes: [] }
  const nodes = new Set(graph.nodes.map((node) => node.id)), outgoing = new Map()
  for (const edge of graph.edges) { const list = outgoing.get(edge.fromNodeId) ?? []; list.push(edge); outgoing.set(edge.fromNodeId, list) }
  const routes = definitions.map((definition) => {
    const { id, startNodeId, endNodeId } = definition
    if (!id || !nodes.has(startNodeId) || !nodes.has(endNodeId)) return { id: id ?? null, status: 'blocked', reason: 'representative-od-node-not-found' }
    const costs = new Map([[startNodeId, 0]]), previous = new Map(), queue = [[0, startNodeId]]
    while (queue.length) { queue.sort((a, b) => a[0] - b[0] || a[1].localeCompare(b[1])); const [cost, node] = queue.shift(); if (cost !== costs.get(node)) continue; if (node === endNodeId) break; for (const edge of outgoing.get(node) ?? []) { const next = cost + edge.walkingSeconds; if (next < (costs.get(edge.toNodeId) ?? Infinity)) { costs.set(edge.toNodeId, next); previous.set(edge.toNodeId, edge); queue.push([next, edge.toNodeId]) } } }
    if (!costs.has(endNodeId)) return { id, status: 'failed', reason: 'representative-od-unreachable' }
    const edgeIds = []; for (let node = endNodeId; node !== startNodeId;) { const edge = previous.get(node); edgeIds.push(edge.id); node = edge.fromNodeId }
    return { id, status: 'passed', startNodeId, endNodeId, walkingSeconds: costs.get(endNodeId), directedEdgeIds: edgeIds.reverse() }
  })
  return { status: routes.every((route) => route.status === 'passed') ? 'passed' : 'failed', reason: null, routes }
}
export function qualityReport(graph, input, representativeOds) {
  const totalMeters = graph.physicalEdges.reduce((sum, edge) => sum + edge.lengthMeters, 0)
  const metersFor = (predicate) => graph.physicalEdges.filter(predicate).reduce((sum, edge) => sum + edge.lengthMeters, 0)
  const fallbackMeters = metersFor((edge) => edge.fallback), explicitMeters = metersFor((edge) => edge.source.confidence === 'explicit'), derivedMeters = metersFor((edge) => edge.source.confidence === 'derived')
  const supportedMeters = explicitMeters + derivedMeters, supportedRatio = totalMeters ? supportedMeters / totalMeters : 0, fallbackRatio = totalMeters ? fallbackMeters / totalMeters : 1
  const representativeOdsResult = representativeOdResults(graph, representativeOds)
  const failures = []
  if (!graph.nodes.length || !graph.edges.length || graph.diagnostics.malformedWayCount) failures.push('empty-or-malformed-graph')
  if (supportedRatio < .8) failures.push('explicit-derived-length-ratio-below-80-percent')
  if (fallbackRatio > .2) failures.push('fallback-length-ratio-above-20-percent')
  if (representativeOdsResult.status === 'failed') failures.push('representative-od-validation-failed')
  return { schemaVersion: 'sidewalk-pedestrian-network-quality-report-2.0', generatedAt: new Date().toISOString(), graphFingerprintSha256: graphFingerprint(graph), input, counts: { nodeCount: graph.nodes.length, physicalEdgeCount: graph.physicalEdges.length, directedEdgeCount: graph.edges.length, fallbackPhysicalEdgeCount: graph.physicalEdges.filter((edge) => edge.fallback).length, fallbackDirectedEdgeCount: graph.edges.filter((edge) => edge.fallback).length, ...graph.diagnostics }, lengthMeters: { total: totalMeters, explicit: explicitMeters, derived: derivedMeters, explicitOrDerived: supportedMeters, fallback: fallbackMeters, explicitOrDerivedRatio: supportedRatio, fallbackRatio }, thresholds: { explicitOrDerivedMinimumRatio: .8, fallbackMaximumRatio: .2 }, representativeOds: representativeOdsResult, validation: { isValid: failures.length === 0, failures } }
}
export function qualitySummary(report) { return { status: report.validation.isValid ? 'accepted' : 'unverified', explicitOrDerivedRatio: report.lengthMeters.explicitOrDerivedRatio, fallbackRatio: report.lengthMeters.fallbackRatio, sourceSchemaVersion: report.input.captureContractVersion ?? 'unknown', validationFailures: [...report.validation.failures] } }
async function main() { const o = parseArgs(process.argv.slice(2)); const [config, osm] = await Promise.all([readFile(resolve(o.config), 'utf8').then(JSON.parse), readFile(resolve(o.osm), 'utf8').then(JSON.parse)]); const graph = buildGraph(config, osm); const fingerprint = graphFingerprint(graph); const report = qualityReport(graph, { areaId: config.areaId, osmPath: o.osm, captureContractVersion: '0.2' }, config.representativeOds); const doc = { schemaVersion: 'environment-cost-pedestrian-network-2.0', areaId: config.areaId, generatedAt: report.generatedAt, graphFingerprintSha256: fingerprint, quality: qualitySummary(report), extent: { center: config.center, radiusMeters: config.radiusMeters }, coordinateReferenceSystem: { geographic: 'EPSG:4326', projected: `EPSG:${6668 + config.coordinateZoneId}` }, nodes: graph.nodes, physicalEdges: graph.physicalEdges, edges: graph.edges }; await Promise.all([mkdir(dirname(resolve(o.output)), { recursive: true }), mkdir(dirname(resolve(o.report)), { recursive: true })]); await writeFile(resolve(o.output), `${JSON.stringify(doc, null, 2)}\n`); await writeFile(resolve(o.report), `${JSON.stringify(report, null, 2)}\n`); if (!report.validation.isValid) throw new Error(report.validation.failures.join(', ')); console.log(`SIDEWALK_GRAPH_BUILT area=${config.areaId} nodes=${graph.nodes.length} physicalEdges=${graph.physicalEdges.length} directedEdges=${graph.edges.length} fingerprint=${fingerprint}`) }
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main().catch((e) => { console.error(e.message); console.error(usage()); process.exitCode = 1 })
