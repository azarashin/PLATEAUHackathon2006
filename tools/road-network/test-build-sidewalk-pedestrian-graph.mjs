import assert from 'node:assert/strict'
import { buildGraph, graphFingerprint, qualityReport, qualitySummary } from './build-sidewalk-pedestrian-graph.mjs'

const config = { areaId: 'fixture', center: [139.7, 35.6], radiusMeters: 1000, coordinateZoneId: 9 }
const way = (id, nodes, geometry, tags) => ({ type: 'way', id, nodes, geometry: geometry.map(([lon, lat]) => ({ lon, lat })), tags })
const osm = { captureContractVersion: '0.2', elements: [
  { type: 'node', id: 1, tags: { crossing: 'marked' } }, { type: 'node', id: 2, tags: {} }, { type: 'node', id: 3, tags: {} }, { type: 'node', id: 4, tags: {} }, { type: 'node', id: 5, tags: {} }, { type: 'node', id: 6, tags: {} },
  { type: 'relation', id: 99, tags: { type: 'route' }, members: [] },
  way(10, [1, 2], [[139.7, 35.6], [139.701, 35.6]], { highway: 'residential', sidewalk: 'both' }),
  way(11, [2, 3], [[139.701, 35.6], [139.702, 35.6]], { highway: 'residential', sidewalk: 'separate' }),
  way(12, [3, 4], [[139.702, 35.6], [139.703, 35.6]], { highway: 'footway' }),
  way(13, [3, 4], [[139.702, 35.6], [139.703, 35.6]], { highway: 'footway', foot: 'no' }),
  way(14, [4, 5], [[139.703, 35.6], [139.704, 35.6]], { highway: 'trunk' }),
  way(15, [4, 5], [[139.703, 35.6], [139.704, 35.6]], { highway: 'motorway' }),
  way(16, [4, 5], [[139.703, 35.6], [139.704, 35.6]], { highway: 'primary', motorroad: 'yes' }),
  way(17, [4, 5], [[139.703, 35.6], [139.704, 35.6]], { highway: 'residential', access: 'private' }),
  way(18, [4, 5], [[139.703, 35.6], [139.704, 35.6]], { highway: 'construction' }),
  way(19, [4, 5], [[139.703, 35.6], [139.704, 35.6]], { highway: 'proposed' }),
  way(20, [4, 5], [[139.703, 35.6], [139.704, 35.6]], { highway: 'raceway' }),
] }
const graph = buildGraph(config, osm)
assert.equal(graph.edges.filter((e) => e.source.id === '10').length, 4, 'sidewalk=both must make two bidirectional offsets')
assert.equal(graph.physicalEdges.filter((e) => e.source.id === '10').length, 2, 'one physical sidewalk segment must back two directed edges')
assert.ok(graph.edges.every((edge) => typeof edge.physicalEdgeId === 'string' && Number.isFinite(edge.walkingSeconds)), 'directed edges must identify their physical segment and walking time')
assert.ok(graph.edges.every((edge) => !('geometry' in edge)), 'directed edges must not duplicate physical geometry')
assert.ok(graph.physicalEdges.every((edge) => Array.isArray(edge.geometry) && Number.isFinite(edge.walkingSeconds)), 'physical segments must carry deterministic geometry and walking time')
const nodesById = new Map(graph.nodes.map((node) => [node.id, node]))
assert.ok(graph.physicalEdges.every((edge) => JSON.stringify(edge.geometry[0]) === JSON.stringify(nodesById.get(edge.fromNodeId).coordinate) && JSON.stringify(edge.geometry.at(-1)) === JSON.stringify(nodesById.get(edge.toNodeId).coordinate)), 'physical geometry endpoints must exactly match canonical nodes')
assert.equal(graph.edges.filter((e) => e.source.id === '11').length, 0, 'sidewalk=separate must not derive duplicate sidewalks')
assert.equal(graph.edges.filter((e) => e.source.id === '12').length, 2, 'independent footway has priority')
assert.equal(graph.edges.some((e) => e.source.id === '13'), false, 'foot=no must be excluded')
assert.equal(graph.edges.filter((e) => e.source.id === '14').length, 2, 'trunk must not be excluded without an explicit pedestrian prohibition')
for (const id of ['15', '16', '17', '18', '19', '20']) assert.equal(graph.edges.some((edge) => edge.source.id === id), false, `unsafe source way ${id} must be excluded`)
assert.deepEqual(graph.diagnostics.excludedWayCountByReason, { 'access-restricted-without-foot-override': 1, 'foot-prohibited': 1, motorroad: 1, 'non-pedestrian-highway': 4 })
assert.ok(graph.edges.some((e) => e.facility === 'crossing' && e.level === 0), 'same-level crossing node connects sidewalk sides')
assert.ok(graph.physicalEdges.every((edge) => typeof edge.source.rationale === 'string'), 'every segment must retain its evidence rationale')
assert.ok(graph.nodes.some((n) => n.side === 'left' && n.coordinate[1] > 35.6), 'left sidewalk must offset north for eastbound way')
const report = qualityReport(graph, { areaId: config.areaId, captureContractVersion: '0.2' }, [{ id: 'fixture-crossing', startNodeId: 'ped:osm-node:1:left:l0', endNodeId: 'ped:osm-node:2:left:l0' }])
const summary = qualitySummary({ ...report, input: { captureContractVersion: '0.2' } })
assert.equal(summary.status, 'accepted')
assert.equal(summary.explicitOrDerivedRatio, report.lengthMeters.explicitOrDerivedRatio)
assert.equal(summary.fallbackRatio, report.lengthMeters.fallbackRatio)
assert.equal(summary.sourceSchemaVersion, '0.2')
assert.equal(summary.qualityContractVersion, 'pedestrian-network-safety-1.1')
assert.deepEqual(summary.validationFailures, report.validation.rejectedReasons)
assert.equal(report.validation.isValid, true)
assert.ok(report.lengthMeters.fallbackRatio > 0, 'shared-space fallback is retained as a reference metric')
assert.equal(report.representativeOds.status, 'passed')
assert.equal(report.processing.excludedWayCountByReason.motorroad, 1)
assert.ok(report.segmentRationales['shared-space-representative-line'].lengthMeters > 0, 'fallback remains a metric, not a rejection')
const missingAudit = qualityReport(graph, {})
assert.equal(missingAudit.validation.status, 'unverified', 'missing mandatory audit input must be unverified')
const invalidTopology = structuredClone(graph); invalidTopology.edges.pop()
assert.equal(qualityReport(invalidTopology, { areaId: config.areaId, captureContractVersion: '0.2' }).validation.status, 'rejected', 'topology corruption must be rejected')
assert.equal(graphFingerprint(graph), graphFingerprint(buildGraph(config, { captureContractVersion: '0.2', elements: [...osm.elements].reverse() })), 'fingerprint must be deterministic')
assert.throws(() => buildGraph(config, { elements: osm.elements.filter((e) => e.type !== 'relation') }), /capture-contract-0.2/, 'way-only capture is rejected')

const untaggedIntersection = { captureContractVersion: '0.2', elements: [
  { type: 'node', id: 100, tags: {} }, { type: 'node', id: 101, tags: {} }, { type: 'node', id: 102, tags: {} }, { type: 'relation', id: 199, tags: {}, members: [] },
  way(100, [100, 101], [[139.71, 35.61], [139.711, 35.61]], { highway: 'residential', sidewalk: 'both' }),
  way(101, [100, 102], [[139.71, 35.61], [139.71, 35.611]], { highway: 'residential', sidewalk: 'both' })
] }
const untaggedGraph = buildGraph(config, untaggedIntersection)
assert.ok(untaggedGraph.physicalEdges.some((edge) => edge.facility === 'intersection-corner'), 'an untagged, same-level intersection must join nearby sidewalk corners')
assert.equal(untaggedGraph.physicalEdges.some((edge) => edge.facility === 'crossing'), false, 'an inferred corner must not be reported as an explicit crossing')
assert.ok(untaggedGraph.diagnostics.intersectionCornerConnectionCount > 0, 'inferred corner count must be recorded')

const separatedIntersection = { captureContractVersion: '0.2', elements: [
  { type: 'node', id: 200, tags: {} }, { type: 'node', id: 201, tags: {} }, { type: 'node', id: 202, tags: {} }, { type: 'relation', id: 299, tags: {}, members: [] },
  way(200, [200, 201], [[139.72, 35.62], [139.721, 35.62]], { highway: 'residential', sidewalk: 'both', level: '0' }),
  way(201, [200, 202], [[139.72, 35.62], [139.72, 35.621]], { highway: 'residential', sidewalk: 'both', level: '1' })
] }
const separatedGraph = buildGraph(config, separatedIntersection)
assert.equal(separatedGraph.physicalEdges.some((edge) => edge.facility === 'intersection-corner'), false, 'different levels must never be joined by inferred corners')
assert.equal(separatedGraph.diagnostics.levelSeparatedIntersectionCornerCandidateCount, 1, 'level-separated candidate evidence must be recorded')

const drawingLayerContinuity = { captureContractVersion: '0.2', elements: [
  { type: 'node', id: 300, tags: {} }, { type: 'node', id: 301, tags: {} }, { type: 'node', id: 302, tags: {} }, { type: 'relation', id: 399, tags: {}, members: [] },
  way(300, [301, 300], [[139.73, 35.63], [139.731, 35.63]], { highway: 'residential', sidewalk: 'left', layer: '0' }),
  way(301, [300, 302], [[139.731, 35.63], [139.732, 35.63]], { highway: 'residential', sidewalk: 'left', layer: '-1' })
] }
const drawingLayerGraph = buildGraph(config, drawingLayerContinuity)
assert.equal(drawingLayerGraph.nodes.filter((node) => node.rawOsmNodeId === 300 && node.side === 'left').length, 1, 'same raw node with matching coordinates must canonicalize despite a drawing-layer transition')
assert.ok(drawingLayerGraph.nodes.some((node) => node.id === 'ped:osm-node:300:left:l0'), 'layer must not become the connection level')
assert.equal(drawingLayerGraph.nodes.some((node) => node.id.includes('l-1')), false, 'drawing layer must not create a separate connection level')
assert.ok(drawingLayerGraph.diagnostics.canonicalSharedRawNodeMergeCount > 0, 'canonical shared-node merges must be diagnosed')
assert.ok(drawingLayerGraph.physicalEdges.every((edge) => edge.lengthMeters > .01), 'canonical merges must not emit zero-length physical edges')
assert.ok(qualityReport(drawingLayerGraph, { areaId: config.areaId, captureContractVersion: '0.2' }).processing.canonicalSharedRawNodeMergeCount > 0, 'quality report must retain canonicalization diagnostics')

const representationMerge = { captureContractVersion: '0.2', elements: [
  { type: 'node', id: 350, tags: {} }, { type: 'node', id: 351, tags: {} }, { type: 'node', id: 352, tags: {} }, { type: 'relation', id: 349, tags: {}, members: [] },
  way(350, [350, 351], [[139.735, 35.635], [139.736, 35.635]], { highway: 'residential' }),
  way(351, [350, 352], [[139.735, 35.635], [139.735, 35.636]], { highway: 'footway' })
] }
const representationMergeGraph = buildGraph(config, representationMerge)
assert.equal(representationMergeGraph.nodes.filter((node) => node.rawOsmNodeId === 350).length, 1, 'coincident centerline and footway representations at one raw node must share a canonical node')
assert.equal(representationMergeGraph.physicalEdges.some((edge) => edge.lengthMeters <= .01), false, 'cross-representation canonicalization must not create a zero-length edge')

const layerTransitionJunction = { captureContractVersion: '0.2', elements: [
  { type: 'node', id: 400, tags: {} }, { type: 'node', id: 401, tags: {} }, { type: 'node', id: 402, tags: {} }, { type: 'relation', id: 499, tags: {}, members: [] },
  way(400, [400, 401], [[139.74, 35.64], [139.741, 35.64]], { highway: 'residential', sidewalk: 'left', layer: '0' }),
  way(401, [400, 402], [[139.74, 35.64], [139.74, 35.641]], { highway: 'residential', sidewalk: 'left', layer: '1' })
] }
const layerTransitionGraph = buildGraph(config, layerTransitionJunction)
assert.equal(layerTransitionGraph.physicalEdges.some((edge) => edge.facility === 'intersection-corner'), false, 'different drawing layers must not produce an inferred corner connector')
assert.ok(layerTransitionGraph.diagnostics.layerIncompatibleIntersectionCornerCandidateCount > 0, 'layer-incompatible inferred-corner candidates must be diagnosed')
assert.ok(layerTransitionGraph.physicalEdges.some((edge) => edge.facility === 'shared-raw-node'), 'a shared raw node must remain connected across a drawing-layer transition')
assert.ok(layerTransitionGraph.diagnostics.sharedRawNodeLayerTransitionConnectionCount > 0, 'successful shared-node layer-transition connectors must be diagnosed')

const detourGraph = {
  nodes: [{ id: 'a', coordinate: [139.73, 35.63] }, { id: 'b', coordinate: [139.731, 35.63] }],
  physicalEdges: [{ id: 'long', fromNodeId: 'a', toNodeId: 'b', geometry: [[139.73, 35.63], [139.731, 35.63]], lengthMeters: 400, walkingSeconds: 400 / 1.4, source: { rationale: 'derived-sidewalk', confidence: 'derived' }, fallback: false }],
  edges: [{ id: 'long:forward', physicalEdgeId: 'long', fromNodeId: 'a', toNodeId: 'b', walkingSeconds: 400 / 1.4 }, { id: 'long:backward', physicalEdgeId: 'long', fromNodeId: 'b', toNodeId: 'a', walkingSeconds: 400 / 1.4 }],
  diagnostics: { malformedWayCount: 0 }
}
const detourReport = qualityReport(detourGraph, { areaId: 'detour-fixture', captureContractVersion: '0.2', requireRepresentativeOds: true }, [{ id: 'short-direct-distance', start: [139.73, 35.63], end: [139.731, 35.63], maxDetourRatio: 2 }])
assert.equal(detourReport.representativeOds.routes[0].reason, 'representative-od-excessive-detour', 'coordinate representative ODs must reject an excessive detour')
assert.equal(detourReport.validation.status, 'rejected', 'excessive representative OD detours must reject the graph')
console.log('SIDEWALK_GRAPH_TEST_PASSED')
