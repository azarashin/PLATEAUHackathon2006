import assert from 'node:assert/strict'
import { buildGraph, graphFingerprint, qualityReport, shortestPath } from './build-pedestrian-graph.mjs'

const config = {
  areaId: 'test-area',
  center: [139.7, 35.6],
  radiusMeters: 1000,
  coordinateZoneId: 9,
  walkingSpeedMetersPerSecond: 1.4,
}
const osm = {
  elements: [
    {
      type: 'way', id: 10, nodes: [100, 101, 102],
      geometry: [{ lon: 139.7, lat: 35.6 }, { lon: 139.7001, lat: 35.6 }, { lon: 139.7002, lat: 35.6 }],
      tags: { highway: 'footway' },
    },
    {
      type: 'way', id: 11, nodes: [100, 101],
      geometry: [{ lon: 139.7, lat: 35.6 }, { lon: 139.7001, lat: 35.6 }],
      tags: { highway: 'footway' },
    },
    {
      type: 'way', id: 12, nodes: [102, 103],
      geometry: [{ lon: 139.7002, lat: 35.6 }, { lon: 139.7003, lat: 35.6 }],
      tags: { highway: 'footway', 'oneway:foot': 'yes' },
    },
    {
      type: 'way', id: 13, nodes: [103, 104],
      geometry: [{ lon: 139.7003, lat: 35.6 }, { lon: 139.7004, lat: 35.6 }],
      tags: { highway: 'footway', foot: 'no' },
    },
    {
      type: 'way', id: 14, nodes: [200, 201],
      geometry: [{ lon: 139.70015, lat: 35.5999 }, { lon: 139.70015, lat: 35.6001 }],
      tags: { highway: 'footway', bridge: 'yes' },
    },
  ],
}
const overrides = { type: 'FeatureCollection', features: [] }

const graph = buildGraph(config, osm, overrides)
const report = qualityReport(graph, { areaId: config.areaId, walkingSpeedMetersPerSecond: 1.4 })

assert.equal(graph.nodes.length, 6, 'OSM node IDs, not coordinate intersections, define topology')
assert.equal(graph.physicalEdgeCount, 4, 'duplicate physical edges must collapse and inaccessible ways must be omitted')
assert.equal(graph.edges.length, 7, 'three bidirectional edges plus one foot one-way edge are expected')
assert.equal(graph.diagnostics.duplicatePhysicalSegmentCount, 1)
assert.equal(graph.diagnostics.excludedWayCount, 1)
assert.equal(graph.edges.filter((edge) => edge.sourceEdgeIds.includes('osm-way-12-0')).length, 1, 'oneway:foot must create one directed edge')
assert.equal(report.validation.isValid, true)
assert.equal(report.connectivity.componentCount, 2, 'a geometric crossing with different OSM node IDs must remain disconnected')

const path = shortestPath(graph, [139.7, 35.6], [139.7003, 35.6])
assert.equal(path.found, true)
assert.equal(path.edgeIds.length, 3)

const reorderedGraph = buildGraph(config, { elements: [...osm.elements].reverse() }, overrides)
assert.equal(graphFingerprint(reorderedGraph), graphFingerprint(graph), 'input ordering must not change stable graph content')
assert.deepEqual(reorderedGraph.nodes.map((node) => node.id), graph.nodes.map((node) => node.id))
assert.deepEqual(reorderedGraph.edges.map((edge) => edge.id), graph.edges.map((edge) => edge.id))

const removalOverrides = {
  type: 'FeatureCollection',
  features: [{
    type: 'Feature',
    geometry: null,
    properties: {
      id: 'test-remove-oneway', areaId: 'test-area', operation: 'remove-edge', sourceEdgeId: 'osm-way-12-0',
      reason: 'test correction', evidence: 'test fixture', createdAt: '2026-08-23', reviewer: 'test',
    },
  }],
}
const correctedGraph = buildGraph(config, osm, removalOverrides)
assert.equal(correctedGraph.diagnostics.removedByOverrideCount, 1)
assert.equal(correctedGraph.appliedOverrides.length, 1)
assert.equal(correctedGraph.edges.some((edge) => edge.sourceEdgeIds.includes('osm-way-12-0')), false)

const malformedGraph = buildGraph(config, { elements: [{ type: 'way', id: 20, geometry: [{ lon: 139.7, lat: 35.6 }, { lon: 139.8, lat: 35.6 }], tags: { highway: 'footway' } }] }, overrides)
assert.equal(qualityReport(malformedGraph, { areaId: config.areaId }).validation.isValid, false, 'missing OSM node IDs must fail validation')

console.log('ROAD_GRAPH_TEST_PASSED')
