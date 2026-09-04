#!/usr/bin/env node

import { createHash } from 'node:crypto'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { geographicToPlane, planeToGeographic } from '../environment-cost-network/japan-plane-rectangular.mjs'

const EARTH = 6371008.8
export const PEDESTRIAN_NETWORK_SAFETY_CONTRACT_VERSION = 'pedestrian-network-safety-1.1'
// `trunk` is deliberately not in this list. It can be a walkable urban arterial;
// only an explicit motor-road/access prohibition makes it non-walkable.
const EXCLUDED_HIGHWAYS = new Set(['motorway', 'motorway_link', 'construction', 'proposed', 'raceway'])
const PEDESTRIAN = new Set(['footway', 'path', 'pedestrian', 'steps', 'crossing'])
const SIDES = { left: ['left'], right: ['right'], both: ['left', 'right'] }

function usage() { return 'Usage: node tools/road-network/build-sidewalk-pedestrian-graph.mjs --config <config.json> --osm <capture-0.2.json> --output <graph.json> --report <report.json>' }
export function parseArgs(args) { const o = {}; for (let i = 0; i < args.length; i += 2) { if (!args[i]?.startsWith('--') || !args[i + 1]) throw new Error(usage()); o[args[i].slice(2)] = args[i + 1] } for (const k of ['config', 'osm', 'output', 'report']) if (!o[k]) throw new Error(`--${k} is required`); return o }
function meters(a, b) { const r = Math.PI / 180; const dlat = (b[1] - a[1]) * r; const dlon = (b[0] - a[0]) * r; const h = Math.sin(dlat / 2) ** 2 + Math.cos(a[1] * r) * Math.cos(b[1] * r) * Math.sin(dlon / 2) ** 2; return 2 * EARTH * Math.asin(Math.sqrt(h)) }
function coordinate(point) { const result = [point?.lon, point?.lat]; if (!result.every(Number.isFinite)) throw new Error('invalid geometry coordinate'); return result }
// `layer` controls rendering order in OSM. It is not a physical connection
// level: only an explicit `level` tag may prevent shared-node connectivity.
function level(tags = {}) { const n = Number(tags.level ?? 0); return Number.isFinite(n) ? n : 0 }
function drawingLayer(tags = {}) { const n = Number(tags.layer ?? 0); return Number.isFinite(n) ? n : 0 }
function walkabilityDecision(tags = {}) {
  if (!tags.highway) return { walkable: false, reason: 'missing-highway' }
  if (EXCLUDED_HIGHWAYS.has(tags.highway) || (tags.construction && tags.construction !== 'no')) return { walkable: false, reason: 'non-pedestrian-highway' }
  if (tags.motorroad === 'yes') return { walkable: false, reason: 'motorroad' }
  if (tags.foot === 'no') return { walkable: false, reason: 'foot-prohibited' }
  if ((tags.access === 'no' || tags.access === 'private') && !['yes', 'designated'].includes(tags.foot)) return { walkable: false, reason: 'access-restricted-without-foot-override' }
  if (tags.area === 'yes') return { walkable: false, reason: 'area-without-linear-walkway' }
  return { walkable: true, reason: null }
}
function offset(a, b, side, zone, width = 2) { const pa = geographicToPlane(a, zone), pb = geographicToPlane(b, zone), dx = pb.eastingMeters - pa.eastingMeters, dy = pb.northingMeters - pa.northingMeters, length = Math.hypot(dx, dy); if (length < .01) return a; const sign = side === 'left' ? 1 : -1; return planeToGeographic({ eastingMeters: pa.eastingMeters + sign * -dy / length * width, northingMeters: pa.northingMeters + sign * dx / length * width }, zone) }
function sourceFor(kind, id, rule, fallback) {
  const rationale = fallback
    ? 'shared-space-representative-line'
    : rule === 'crossing-tag'
      ? 'explicit-crossing'
    : rule === 'intersection-corner'
      ? 'derived-intersection-corner'
      : rule === 'shared-raw-node-layer-transition'
        ? 'shared-raw-node-topology'
      : rule.startsWith('highway=')
        ? 'explicit-pedestrian-facility'
        : 'derived-sidewalk'
  return { kind, id: String(id), rule, confidence: fallback ? 'fallback' : rationale.startsWith('explicit-') ? 'explicit' : 'derived', rationale }
}
function stable(value) { return JSON.stringify(value) }

export function buildGraph(config, osm) {
  if (!Array.isArray(osm?.elements)) throw new Error('OSM input does not contain elements')
  const types = new Set(osm.elements.map((e) => e?.type)); if (osm.captureContractVersion !== '0.2' || !types.has('way') || !types.has('node')) throw new Error('capture-contract-0.2 required: way, node, and relation-query marker; way-only snapshot is not accepted')
  const nodeTags = new Map(osm.elements.filter((e) => e.type === 'node').map((e) => [e.id, e.tags ?? {}]))
  const nodes = new Map(), edges = [], physicalEdges = [], nodeApproaches = new Map(), nodeVariants = new Map(), diagnostics = { sourceWayCount: 0, includedWayCount: 0, excludedWayCountByReason: {}, excludedWayCount: 0, explicitWayCount: 0, derivedSidewalkWayCount: 0, separateSidewalkSkippedCount: 0, fallbackWayCount: 0, crossingConnectionCount: 0, levelSeparatedCrossingConnectionCount: 0, intersectionCornerConnectionCount: 0, levelSeparatedIntersectionCornerCandidateCount: 0, canonicalSharedRawNodeMergeCount: 0, sharedRawNodeCoordinateVariantCount: 0, sharedRawNodeLayerTransitionConnectionCount: 0, layerIncompatibleIntersectionCornerCandidateCount: 0, malformedWayCount: 0 }
  const addNode = (id, coord, rawId, side, z, osmLayer, kind, source) => {
    // Connectivity belongs to the raw OSM node and explicit physical level,
    // not to our derived representation (`left`, `right`, `center`, or
    // `none`). A footway and a fallback/derived representation at the same
    // coordinate must therefore share one canonical graph node.
    const canonicalKey = `${rawId}|${z}`
    const variants = nodeVariants.get(canonicalKey) ?? []
    const existing = variants.find((candidate) => meters(candidate.coordinate, coord) <= .01)
    if (existing) { diagnostics.canonicalSharedRawNodeMergeCount++; return existing.id }
    const variantId = variants.length === 0 ? id : `${id}:v${variants.length}`
    const node = { id: variantId, coordinate: coord, zLevel: z, osmLayer, kind, source, rawOsmNodeId: rawId, side }
    variants.push(node); nodeVariants.set(canonicalKey, variants); nodes.set(variantId, node)
    if (variants.length > 1) diagnostics.sharedRawNodeCoordinateVariantCount++
    return variantId
  }
  const addBidirectional = (base, from, to, geometry, detail) => {
    const length = meters(geometry[0], geometry[1])
    if (length <= .01 || from === to) return false
    const physicalEdge = { id: base, fromNodeId: from, toNodeId: to, geometry, lengthMeters: length, walkingSeconds: length / 1.4, walkability: 'walkable', ...detail }
    physicalEdges.push(physicalEdge)
    for (const [suffix, a, b] of [['forward', from, to], ['backward', to, from]]) edges.push({ id: `${base}:${suffix}`, physicalEdgeId: base, fromNodeId: a, toNodeId: b, walkingSeconds: physicalEdge.walkingSeconds, walkability: 'walkable', facility: detail.facility, side: detail.side, level: detail.level, source: detail.source, fallback: detail.fallback })
    return true
  }
  const addApproach = (nodeId, wayId, from, to) => {
    const list = nodeApproaches.get(nodeId) ?? []
    list.push({ wayId: String(wayId), from, to })
    nodeApproaches.set(nodeId, list)
  }
  const ways = osm.elements.filter((e) => e.type === 'way').sort((a, b) => a.id - b.id)
  for (const way of ways) {
    diagnostics.sourceWayCount++; const tags = way.tags ?? {}, highway = tags.highway, decision = walkabilityDecision(tags)
    if (!decision.walkable) { diagnostics.excludedWayCount++; diagnostics.excludedWayCountByReason[decision.reason] = (diagnostics.excludedWayCountByReason[decision.reason] ?? 0) + 1; continue }
    if (!Array.isArray(way.nodes) || !Array.isArray(way.geometry) || way.nodes.length !== way.geometry.length || way.nodes.length < 2) { diagnostics.malformedWayCount++; continue }
    const explicit = PEDESTRIAN.has(highway), sidewalk = tags.sidewalk
    if (!explicit && sidewalk === 'separate') { diagnostics.separateSidewalkSkippedCount++; continue }
    diagnostics.includedWayCount++
    const variants = explicit ? [{ side: 'none', facility: highway, rule: `highway=${highway}`, fallback: false }] : (SIDES[sidewalk]?.map((side) => ({ side, facility: 'sidewalk', rule: `sidewalk=${sidewalk}`, fallback: false })) ?? [{ side: 'center', facility: 'centerline', rule: 'v1-centerline-fallback', fallback: true }])
    if (explicit) diagnostics.explicitWayCount++; else if (variants[0].fallback) diagnostics.fallbackWayCount++; else diagnostics.derivedSidewalkWayCount++
    for (const variant of variants) for (let i = 0; i < way.nodes.length - 1; i++) {
      let a, b; try { a = coordinate(way.geometry[i]); b = coordinate(way.geometry[i + 1]) } catch { diagnostics.malformedWayCount++; continue }
      const z = level(tags), osmLayer = drawingLayer(tags), aa = variant.side === 'left' || variant.side === 'right' ? offset(a, b, variant.side, config.coordinateZoneId) : a
      const bb = (() => { if (variant.side !== 'left' && variant.side !== 'right') return b; const pa = geographicToPlane(a, config.coordinateZoneId), pb = geographicToPlane(b, config.coordinateZoneId), shifted = geographicToPlane(aa, config.coordinateZoneId); return planeToGeographic({ eastingMeters: pb.eastingMeters + shifted.eastingMeters - pa.eastingMeters, northingMeters: pb.northingMeters + shifted.northingMeters - pa.northingMeters }, config.coordinateZoneId) })()
      const from = addNode(`ped:osm-node:${way.nodes[i]}:${variant.side}:l${z}`, aa, way.nodes[i], variant.side, z, osmLayer, variant.facility === 'centerline' ? 'fallback-junction' : 'sidewalk-junction', sourceFor(variant.fallback ? 'v1-centerline' : 'osm-node', way.nodes[i], variant.rule, variant.fallback))
      const to = addNode(`ped:osm-node:${way.nodes[i + 1]}:${variant.side}:l${z}`, bb, way.nodes[i + 1], variant.side, z, osmLayer, variant.facility === 'centerline' ? 'fallback-junction' : 'sidewalk-junction', sourceFor(variant.fallback ? 'v1-centerline' : 'osm-node', way.nodes[i + 1], variant.rule, variant.fallback))
      // A node shared by differently oriented source segments has one canonical
      // coordinate.  Physical-edge endpoints must use it too, otherwise a few-cm
      // offset mismatch makes the graph non-portable to the server bundle.
      addBidirectional(`ped:way:${way.id}:${variant.side}:${i}`, from, to, [nodes.get(from).coordinate, nodes.get(to).coordinate], { facility: variant.facility, side: variant.side, level: z, crossing: null, source: sourceFor(variant.fallback ? 'v1-centerline' : 'osm-way', way.id, variant.rule, variant.fallback), fallback: variant.fallback })
      addApproach(from, way.id, a, b)
      addApproach(to, way.id, b, a)
    }
  }
  // A tagged crossing node may connect different side variants, but never different levels.
  const groups = new Map(), crossingLevels = new Map(); for (const node of nodes.values()) { const key = `${node.rawOsmNodeId}|${node.zLevel}`; const list = groups.get(key) ?? []; list.push(node); groups.set(key, list); const levels = crossingLevels.get(node.rawOsmNodeId) ?? new Set(); levels.add(node.zLevel); crossingLevels.set(node.rawOsmNodeId, levels) }
  for (const [key, group] of groups) { const [raw] = key.split('|'); const tags = nodeTags.get(Number(raw)) ?? {}; if (!('crossing' in tags || tags.highway === 'crossing')) continue; group.sort((a, b) => a.id.localeCompare(b.id)); for (let i = 1; i < group.length; i++) { const a = group[0], b = group[i]; if (a.side === b.side) continue; if (addBidirectional(`ped:crossing:${raw}:l${a.zLevel}:${i}`, a.id, b.id, [a.coordinate, b.coordinate], { facility: 'crossing', side: 'none', level: a.zLevel, crossing: String(tags.crossing ?? 'yes'), source: sourceFor('osm-node', raw, 'crossing-tag', false), fallback: false })) diagnostics.crossingConnectionCount++ } }
  for (const [raw, levels] of crossingLevels) { const tags = nodeTags.get(Number(raw)) ?? {}; if (('crossing' in tags || tags.highway === 'crossing') && levels.size > 1) diagnostics.levelSeparatedCrossingConnectionCount += levels.size - 1 }
  // Untagged junctions are common in OSM. Join only nearby sidewalk/fallback
  // corners from non-collinear source ways. This is not a generic road crossing:
  // explicit crossing tags take precedence and levels are never mixed.
  // Derived sidewalks are offset 2 m from the road centerline, so opposite
  // corner representatives can be 4 m apart at a shared OSM junction.
  const maximumCornerMeters = Number(config.intersectionCornerConnectorMaxMeters ?? 4.5)
  if (!Number.isFinite(maximumCornerMeters) || maximumCornerMeters <= 0) throw new Error('intersectionCornerConnectorMaxMeters must be a positive number')
  const nonCollinear = (a, b) => {
    const pa = geographicToPlane(a.from, config.coordinateZoneId), qa = geographicToPlane(a.to, config.coordinateZoneId), pb = geographicToPlane(b.from, config.coordinateZoneId), qb = geographicToPlane(b.to, config.coordinateZoneId)
    const ax = qa.eastingMeters - pa.eastingMeters, ay = qa.northingMeters - pa.northingMeters, bx = qb.eastingMeters - pb.eastingMeters, by = qb.northingMeters - pb.northingMeters
    const lengths = Math.hypot(ax, ay) * Math.hypot(bx, by)
    return lengths > .01 && Math.abs((ax * bx + ay * by) / lengths) < .9
  }
  for (const [key, group] of groups) {
    const [raw] = key.split('|'), tags = nodeTags.get(Number(raw)) ?? {}
    if ('crossing' in tags || tags.highway === 'crossing') continue
    const sorted = [...group].sort((a, b) => a.id.localeCompare(b.id))
    for (let i = 0; i < sorted.length; i++) for (let j = i + 1; j < sorted.length; j++) {
      const a = sorted[i], b = sorted[j]
      // `side` is relative to each source way's direction.  At a right-angle
      // junction, the same physical corner can therefore be labelled `left`
      // on both ways.  The non-collinear, different-way and same-level checks
      // below are the safety conditions; do not use the relative side label to
      // reject that valid corner connection.
      if (a.osmLayer !== b.osmLayer) { diagnostics.layerIncompatibleIntersectionCornerCandidateCount++; continue }
      if (meters(a.coordinate, b.coordinate) > maximumCornerMeters) continue
      const approachesA = nodeApproaches.get(a.id) ?? [], approachesB = nodeApproaches.get(b.id) ?? []
      if (!approachesA.some((left) => approachesB.some((right) => left.wayId !== right.wayId && nonCollinear(left, right)))) continue
      if (addBidirectional(`ped:intersection-corner:${raw}:l${a.zLevel}:${i}-${j}`, a.id, b.id, [a.coordinate, b.coordinate], { facility: 'intersection-corner', side: 'none', level: a.zLevel, crossing: null, source: sourceFor('osm-node', raw, 'intersection-corner', false), fallback: false })) diagnostics.intersectionCornerConnectionCount++
    }
  }
  // A raw OSM node remains a topological junction when only its drawing layer
  // changes. Connect coordinate variants on the same explicit level; explicit
  // `level` differences remain separate through the group key above.
  for (const [canonicalKey, variants] of nodeVariants) {
    if (variants.length < 2) continue
    for (let leftIndex = 0; leftIndex < variants.length; leftIndex++) for (let rightIndex = leftIndex + 1; rightIndex < variants.length; rightIndex++) {
      const left = variants[leftIndex], right = variants[rightIndex]
      if (left.side !== right.side || left.osmLayer === right.osmLayer) continue
      if (addBidirectional(`ped:shared-raw-node:${canonicalKey}:${leftIndex}-${rightIndex}`, left.id, right.id, [left.coordinate, right.coordinate], { facility: 'shared-raw-node', side: left.side, level: left.zLevel, crossing: null, source: sourceFor('osm-node', left.rawOsmNodeId, 'shared-raw-node-layer-transition', false), fallback: false })) diagnostics.sharedRawNodeLayerTransitionConnectionCount++
    }
  }
  for (const [, levels] of crossingLevels) if (levels.size > 1) diagnostics.levelSeparatedIntersectionCornerCandidateCount += levels.size - 1
  const output = { nodes: [...nodes.values()].sort((a, b) => a.id.localeCompare(b.id)), physicalEdges: physicalEdges.sort((a, b) => a.id.localeCompare(b.id)), edges: edges.sort((a, b) => a.id.localeCompare(b.id)), diagnostics }
  return output
}
export function graphFingerprint(graph) { return createHash('sha256').update(stable({ nodes: graph.nodes.map((n) => [n.id, n.coordinate, n.zLevel, n.side]), physicalEdges: graph.physicalEdges.map((e) => [e.id, e.fromNodeId, e.toNodeId, e.geometry, e.lengthMeters, e.walkingSeconds, e.facility, e.side, e.level, e.fallback]), edges: graph.edges.map((e) => [e.id, e.physicalEdgeId, e.fromNodeId, e.toNodeId, e.walkingSeconds]) })).digest('hex') }
function representativeOdResults(graph, definitions = []) {
  if (!Array.isArray(definitions) || definitions.length === 0) return { status: 'blocked', reason: 'representative-od-not-configured', routes: [] }
  const nodesById = new Map(graph.nodes.map((node) => [node.id, node])), outgoing = new Map()
  for (const edge of graph.edges) { const list = outgoing.get(edge.fromNodeId) ?? []; list.push(edge); outgoing.set(edge.fromNodeId, list) }
  const validCoordinate = (value) => Array.isArray(value) && value.length === 2 && value.every(Number.isFinite)
  const snap = (coordinate, maximumSnapMeters) => {
    if (!validCoordinate(coordinate)) return null
    let candidate = null
    for (const node of nodesById.values()) {
      const distanceMeters = meters(coordinate, node.coordinate)
      if (!candidate || distanceMeters < candidate.distanceMeters || (distanceMeters === candidate.distanceMeters && node.id.localeCompare(candidate.node.id) < 0)) candidate = { node, distanceMeters }
    }
    return candidate && candidate.distanceMeters <= maximumSnapMeters ? candidate : null
  }
  const routes = definitions.map((definition) => {
    const id = definition?.id ?? null, maximumSnapMeters = Number(definition?.maxSnapDistanceMeters ?? 75), maximumDetourRatio = Number(definition?.maxDetourRatio ?? 2.5)
    if (!id || !Number.isFinite(maximumSnapMeters) || maximumSnapMeters <= 0 || !Number.isFinite(maximumDetourRatio) || maximumDetourRatio < 1) return { id, status: 'failed', reason: 'representative-od-invalid-definition' }
    const startSnap = definition.startNodeId ? (nodesById.has(definition.startNodeId) ? { node: nodesById.get(definition.startNodeId), distanceMeters: 0 } : null) : snap(definition.start, maximumSnapMeters)
    const endSnap = definition.endNodeId ? (nodesById.has(definition.endNodeId) ? { node: nodesById.get(definition.endNodeId), distanceMeters: 0 } : null) : snap(definition.end, maximumSnapMeters)
    if (!startSnap || !endSnap) return { id, status: 'failed', reason: definition.startNodeId || definition.endNodeId ? 'representative-od-node-not-found' : 'representative-od-snap-too-far' }
    const { node: startNode } = startSnap, { node: endNode } = endSnap
    const directDistanceMeters = meters(startNode.coordinate, endNode.coordinate)
    if (startNode.id === endNode.id || directDistanceMeters <= .01) return { id, status: 'failed', reason: 'representative-od-degenerate', startNodeId: startNode.id, endNodeId: endNode.id }
    const costs = new Map([[startNode.id, 0]]), previous = new Map(), queue = [[0, startNode.id]]
    while (queue.length) { queue.sort((a, b) => a[0] - b[0] || a[1].localeCompare(b[1])); const [cost, node] = queue.shift(); if (cost !== costs.get(node)) continue; if (node === endNode.id) break; for (const edge of outgoing.get(node) ?? []) { const next = cost + edge.walkingSeconds; if (next < (costs.get(edge.toNodeId) ?? Infinity)) { costs.set(edge.toNodeId, next); previous.set(edge.toNodeId, edge); queue.push([next, edge.toNodeId]) } } }
    if (!costs.has(endNode.id)) return { id, status: 'failed', reason: 'representative-od-unreachable', startNodeId: startNode.id, endNodeId: endNode.id }
    const edgeIds = []; for (let node = endNode.id; node !== startNode.id;) { const edge = previous.get(node); edgeIds.push(edge.id); node = edge.fromNodeId }
    const walkingSeconds = costs.get(endNode.id), routeDistanceMeters = walkingSeconds * 1.4, detourRatio = routeDistanceMeters / directDistanceMeters
    const evidence = { id, startNodeId: startNode.id, endNodeId: endNode.id, startSnapDistanceMeters: startSnap.distanceMeters, endSnapDistanceMeters: endSnap.distanceMeters, directDistanceMeters, routeDistanceMeters, detourRatio, maxDetourRatio: maximumDetourRatio, walkingSeconds, directedEdgeIds: edgeIds.reverse() }
    return detourRatio <= maximumDetourRatio ? { ...evidence, status: 'passed' } : { ...evidence, status: 'failed', reason: 'representative-od-excessive-detour' }
  })
  return { status: routes.every((route) => route.status === 'passed') ? 'passed' : 'failed', reason: null, routes }
}
function topologyAudit(graph) {
  const rejected = [], nodeIds = new Set(graph.nodes.map((node) => node.id)), nodesById = new Map(graph.nodes.map((node) => [node.id, node])), physicalById = new Map(), directedByPhysical = new Map(), edgeIds = new Set()
  for (const edge of graph.physicalEdges) {
    if (physicalById.has(edge.id)) rejected.push('topology-duplicate-physical-edge-id')
    physicalById.set(edge.id, edge)
    if (!nodeIds.has(edge.fromNodeId) || !nodeIds.has(edge.toNodeId) || edge.fromNodeId === edge.toNodeId) rejected.push('topology-invalid-physical-edge-endpoint')
    const from = nodesById.get(edge.fromNodeId), to = nodesById.get(edge.toNodeId)
    const endpointsMatch = from && to && edge.geometry?.length >= 2 && edge.geometry[0]?.every((value, index) => Math.abs(value - from.coordinate[index]) <= 1e-9) && edge.geometry[edge.geometry.length - 1]?.every((value, index) => Math.abs(value - to.coordinate[index]) <= 1e-9)
    if (!Array.isArray(edge.geometry) || edge.geometry.length < 2 || !edge.geometry.every((point) => Array.isArray(point) && point.length === 2 && point.every(Number.isFinite)) || !endpointsMatch || !Number.isFinite(edge.lengthMeters) || edge.lengthMeters <= 0 || !Number.isFinite(edge.walkingSeconds) || edge.walkingSeconds <= 0) rejected.push('topology-invalid-physical-edge-geometry')
    if (!edge.source?.rationale || !edge.source?.confidence) rejected.push('missing-segment-rationale')
  }
  for (const edge of graph.edges) {
    if (edgeIds.has(edge.id)) rejected.push('topology-duplicate-directed-edge-id')
    edgeIds.add(edge.id)
    const physical = physicalById.get(edge.physicalEdgeId)
    if (!physical) { rejected.push('topology-directed-edge-without-physical-edge'); continue }
    if (!((edge.fromNodeId === physical.fromNodeId && edge.toNodeId === physical.toNodeId) || (edge.fromNodeId === physical.toNodeId && edge.toNodeId === physical.fromNodeId))) rejected.push('topology-directed-edge-endpoint-mismatch')
    const directions = directedByPhysical.get(edge.physicalEdgeId) ?? []; directions.push(edge); directedByPhysical.set(edge.physicalEdgeId, directions)
  }
  for (const [physicalEdgeId, directions] of directedByPhysical) {
    const physical = physicalById.get(physicalEdgeId)
    if (directions.length !== 2 || !directions.some((edge) => edge.fromNodeId === physical.fromNodeId && edge.toNodeId === physical.toNodeId) || !directions.some((edge) => edge.fromNodeId === physical.toNodeId && edge.toNodeId === physical.fromNodeId)) rejected.push('topology-physical-edge-missing-direction')
  }
  return { isSafe: rejected.length === 0, rejected: [...new Set(rejected)].sort() }
}
function reasonBreakdown(graph) {
  const result = {}
  for (const edge of graph.physicalEdges) {
    const rationale = edge.source?.rationale ?? 'missing'
    const entry = result[rationale] ?? { physicalEdgeCount: 0, lengthMeters: 0 }
    entry.physicalEdgeCount++; entry.lengthMeters += edge.lengthMeters; result[rationale] = entry
  }
  return result
}
export function qualityReport(graph, input = {}, representativeOds) {
  const totalMeters = graph.physicalEdges.reduce((sum, edge) => sum + edge.lengthMeters, 0)
  const metersFor = (predicate) => graph.physicalEdges.filter(predicate).reduce((sum, edge) => sum + edge.lengthMeters, 0)
  const fallbackMeters = metersFor((edge) => edge.fallback), explicitMeters = metersFor((edge) => edge.source.confidence === 'explicit'), derivedMeters = metersFor((edge) => edge.source.confidence === 'derived')
  const supportedMeters = explicitMeters + derivedMeters, supportedRatio = totalMeters ? supportedMeters / totalMeters : 0, fallbackRatio = totalMeters ? fallbackMeters / totalMeters : 1
  const representativeOdsResult = representativeOdResults(graph, representativeOds)
  const topology = topologyAudit(graph), rejectedReasons = [...topology.rejected]
  if (!graph.nodes.length || !graph.edges.length) rejectedReasons.push('empty-pedestrian-graph')
  if (graph.diagnostics.malformedWayCount) rejectedReasons.push('malformed-source-way')
  if (representativeOdsResult.status === 'failed') rejectedReasons.push('representative-od-validation-failed')
  if (input.requireRepresentativeOds && representativeOdsResult.status !== 'passed') rejectedReasons.push('representative-od-required')
  const unverifiedReasons = []
  if (!input.areaId) unverifiedReasons.push('missing-area-id')
  if (!input.captureContractVersion) unverifiedReasons.push('missing-capture-contract-version')
  if (representativeOdsResult.status !== 'passed') unverifiedReasons.push('representative-od-not-validated')
  const warnings = representativeOdsResult.status === 'blocked' ? ['representative-od-not-configured'] : []
  const status = rejectedReasons.length ? 'rejected' : unverifiedReasons.length ? 'unverified' : 'accepted'
  const counts = { nodeCount: graph.nodes.length, physicalEdgeCount: graph.physicalEdges.length, directedEdgeCount: graph.edges.length, fallbackPhysicalEdgeCount: graph.physicalEdges.filter((edge) => edge.fallback).length, fallbackDirectedEdgeCount: graph.edges.filter((edge) => edge.fallback).length, ...graph.diagnostics }
  return {
    schemaVersion: 'sidewalk-pedestrian-network-quality-report-2.0',
    qualityContractVersion: PEDESTRIAN_NETWORK_SAFETY_CONTRACT_VERSION,
    generatedAt: new Date().toISOString(), graphFingerprintSha256: graphFingerprint(graph), input, counts,
    processing: { sourceWayCount: graph.diagnostics.sourceWayCount, includedWayCount: graph.diagnostics.includedWayCount, excludedWayCount: graph.diagnostics.excludedWayCount, excludedWayCountByReason: graph.diagnostics.excludedWayCountByReason, separateSidewalkSkippedCount: graph.diagnostics.separateSidewalkSkippedCount, malformedWayCount: graph.diagnostics.malformedWayCount, crossingConnectionCount: graph.diagnostics.crossingConnectionCount, levelSeparatedCrossingConnectionCount: graph.diagnostics.levelSeparatedCrossingConnectionCount, intersectionCornerConnectionCount: graph.diagnostics.intersectionCornerConnectionCount, levelSeparatedIntersectionCornerCandidateCount: graph.diagnostics.levelSeparatedIntersectionCornerCandidateCount, canonicalSharedRawNodeMergeCount: graph.diagnostics.canonicalSharedRawNodeMergeCount, sharedRawNodeCoordinateVariantCount: graph.diagnostics.sharedRawNodeCoordinateVariantCount, sharedRawNodeLayerTransitionConnectionCount: graph.diagnostics.sharedRawNodeLayerTransitionConnectionCount, layerIncompatibleIntersectionCornerCandidateCount: graph.diagnostics.layerIncompatibleIntersectionCornerCandidateCount },
    lengthMeters: { total: totalMeters, explicit: explicitMeters, derived: derivedMeters, explicitOrDerived: supportedMeters, fallback: fallbackMeters, explicitOrDerivedRatio: supportedRatio, fallbackRatio },
    segmentRationales: reasonBreakdown(graph), topology, representativeOds: representativeOdsResult,
    validation: { status, isValid: status === 'accepted', rejectedReasons: [...new Set(rejectedReasons)].sort(), unverifiedReasons, warnings }
  }
}
export function qualitySummary(report) { return { qualityContractVersion: report.qualityContractVersion, status: report.validation.status, explicitOrDerivedRatio: report.lengthMeters.explicitOrDerivedRatio, fallbackRatio: report.lengthMeters.fallbackRatio, sourceSchemaVersion: report.input.captureContractVersion ?? 'unknown', validationFailures: [...report.validation.rejectedReasons], validationWarnings: [...report.validation.warnings] } }
async function main() { const o = parseArgs(process.argv.slice(2)); const [config, osm] = await Promise.all([readFile(resolve(o.config), 'utf8').then(JSON.parse), readFile(resolve(o.osm), 'utf8').then(JSON.parse)]); const graph = buildGraph(config, osm); const fingerprint = graphFingerprint(graph); const report = qualityReport(graph, { areaId: config.areaId, osmPath: o.osm, captureContractVersion: '0.2', requireRepresentativeOds: true }, config.representativeOds); const doc = { schemaVersion: 'environment-cost-pedestrian-network-2.0', areaId: config.areaId, generatedAt: report.generatedAt, graphFingerprintSha256: fingerprint, quality: qualitySummary(report), extent: { center: config.center, radiusMeters: config.radiusMeters }, coordinateReferenceSystem: { geographic: 'EPSG:4326', projected: `EPSG:${6668 + config.coordinateZoneId}` }, nodes: graph.nodes, physicalEdges: graph.physicalEdges, edges: graph.edges }; await Promise.all([mkdir(dirname(resolve(o.output)), { recursive: true }), mkdir(dirname(resolve(o.report)), { recursive: true })]); await writeFile(resolve(o.output), `${JSON.stringify(doc, null, 2)}\n`); await writeFile(resolve(o.report), `${JSON.stringify(report, null, 2)}\n`); if (report.validation.status === 'rejected') throw new Error(report.validation.rejectedReasons.join(', ')); console.log(`SIDEWALK_GRAPH_BUILT area=${config.areaId} status=${report.validation.status} nodes=${graph.nodes.length} physicalEdges=${graph.physicalEdges.length} directedEdges=${graph.edges.length} fingerprint=${fingerprint}`) }
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main().catch((e) => { console.error(e.message); console.error(usage()); process.exitCode = 1 })
