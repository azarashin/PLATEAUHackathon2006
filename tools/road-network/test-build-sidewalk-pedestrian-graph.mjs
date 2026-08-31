import assert from 'node:assert/strict'
import { buildGraph, graphFingerprint, qualityReport } from './build-sidewalk-pedestrian-graph.mjs'

const config = { areaId: 'fixture', center: [139.7, 35.6], radiusMeters: 1000, coordinateZoneId: 9 }
const way = (id, nodes, geometry, tags) => ({ type: 'way', id, nodes, geometry: geometry.map(([lon, lat]) => ({ lon, lat })), tags })
const osm = { captureContractVersion: '0.2', elements: [
  { type: 'node', id: 1, tags: { crossing: 'marked' } }, { type: 'node', id: 2, tags: {} }, { type: 'node', id: 3, tags: {} }, { type: 'node', id: 4, tags: {} },
  { type: 'relation', id: 99, tags: { type: 'route' }, members: [] },
  way(10, [1, 2], [[139.7, 35.6], [139.701, 35.6]], { highway: 'residential', sidewalk: 'both' }),
  way(11, [2, 3], [[139.701, 35.6], [139.702, 35.6]], { highway: 'residential', sidewalk: 'separate' }),
  way(12, [3, 4], [[139.702, 35.6], [139.703, 35.6]], { highway: 'footway' }),
  way(13, [3, 4], [[139.702, 35.6], [139.703, 35.6]], { highway: 'footway', foot: 'no' }),
] }
const graph = buildGraph(config, osm)
assert.equal(graph.edges.filter((e) => e.source.id === '10').length, 4, 'sidewalk=both must make two bidirectional offsets')
assert.equal(graph.physicalEdges.filter((e) => e.source.id === '10').length, 2, 'one physical sidewalk segment must back two directed edges')
assert.ok(graph.edges.every((edge) => typeof edge.physicalEdgeId === 'string' && Number.isFinite(edge.walkingSeconds)), 'directed edges must identify their physical segment and walking time')
assert.ok(graph.edges.every((edge) => !('geometry' in edge)), 'directed edges must not duplicate physical geometry')
assert.ok(graph.physicalEdges.every((edge) => Array.isArray(edge.geometry) && Number.isFinite(edge.walkingSeconds)), 'physical segments must carry deterministic geometry and walking time')
assert.equal(graph.edges.filter((e) => e.source.id === '11').length, 0, 'sidewalk=separate must not derive duplicate sidewalks')
assert.equal(graph.edges.filter((e) => e.source.id === '12').length, 2, 'independent footway has priority')
assert.equal(graph.edges.some((e) => e.source.id === '13'), false, 'foot=no must be excluded')
assert.ok(graph.edges.some((e) => e.facility === 'crossing' && e.level === 0), 'same-level crossing node connects sidewalk sides')
assert.ok(graph.nodes.some((n) => n.side === 'left' && n.coordinate[1] > 35.6), 'left sidewalk must offset north for eastbound way')
const report = qualityReport(graph, {}, [{ id: 'fixture-crossing', startNodeId: 'ped:osm-node:1:left:l0', endNodeId: 'ped:osm-node:2:left:l0' }])
assert.equal(report.validation.isValid, true)
assert.equal(report.lengthMeters.fallbackRatio, 0)
assert.equal(report.representativeOds.status, 'passed')
assert.equal(graphFingerprint(graph), graphFingerprint(buildGraph(config, { captureContractVersion: '0.2', elements: [...osm.elements].reverse() })), 'fingerprint must be deterministic')
assert.throws(() => buildGraph(config, { elements: osm.elements.filter((e) => e.type !== 'relation') }), /capture-contract-0.2/, 'way-only capture is rejected')
console.log('SIDEWALK_GRAPH_TEST_PASSED')
