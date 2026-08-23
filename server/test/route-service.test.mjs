import assert from 'node:assert/strict'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import { createRouteFixtureInputs } from '../fixtures/route-fixture-inputs.mjs'
import { RouteError } from '../src/route-error.mjs'
import { RouteService } from '../src/route-service.mjs'

const manifestPath = fileURLToPath(new URL('../../data/fixtures/route-server-bundle-v1/manifest.json', import.meta.url))
const missingManifestPath = fileURLToPath(new URL('../../data/fixtures/environment-cost-server-bundle-v1/manifest.json', import.meta.url))
const fixture = createRouteFixtureInputs()
const service = await RouteService.load([{ manifestPath, maximumSnapDistanceMeters: 100 }])

function request(overrides = {}) {
  return {
    areaId: 'route-server-fixture',
    timestamp: fixture.timestamp,
    start: fixture.coordinates.start,
    end: fixture.coordinates.end,
    ...overrides,
  }
}

function approximately(actual, expected, tolerance = 1e-9) {
  assert.ok(Math.abs(actual - expected) <= tolerance, `${actual} is not within ${tolerance} of ${expected}`)
}

test('three profiles select shortest, balanced, and shaded paths', () => {
  const result = service.compare(request())
  assert.equal(result.schemaVersion, 'route-response-1.0')
  assert.equal(result.missingCostPolicy, 'assume-fully-sun-and-report-unknown-coverage')
  assert.equal(result.presentation.kpiLabels.unknownWalkingSeconds, '不明な歩行時間')
  assert.equal(result.snapped.start.nodeId, 'osm-node-2001')
  assert.equal(result.snapped.end.nodeId, 'osm-node-2005')
  assert.deepEqual(result.routes.map((route) => route.profile), [
    { id: 'shortest', solarAvoidanceFactor: 0 },
    { id: 'balanced', solarAvoidanceFactor: 0.5 },
    { id: 'shade', solarAvoidanceFactor: 2 },
  ])
  assert.deepEqual(result.routes[0].edgeIds, ['osm-way-201-0:forward', 'osm-way-202-0:forward'])
  assert.deepEqual(result.routes[1].edgeIds, ['osm-way-203-0:forward', 'osm-way-204-0:forward'])
  assert.deepEqual(result.routes[2].edgeIds, ['osm-way-205-0:forward', 'osm-way-206-0:forward'])
  assert.deepEqual(result.routes.map((route) => route.kpis.walkingSeconds), [200, 230, 300])
  approximately(result.routes[0].kpis.solarExposureSeconds, 180)
  approximately(result.routes[1].kpis.solarExposureSeconds, 115)
  approximately(result.routes[2].kpis.solarExposureSeconds, 15)
  assert.ok(result.routes[0].kpis.solarExposureSeconds >= result.routes[1].kpis.solarExposureSeconds)
  assert.ok(result.routes[1].kpis.solarExposureSeconds >= result.routes[2].kpis.solarExposureSeconds)
  assert.equal(result.routes.every((route) => route.kpis.coverageStatus === 'available'), true)
  assert.equal('nodes' in result, false)
  assert.equal('costsByTimestamp' in result, false)
})

test('road edge details match the formal cost slice and aggregate to route KPIs', () => {
  const compared = service.compare(request())
  for (const route of compared.routes) {
    const edges = service.roadEdges({
      areaId: 'route-server-fixture',
      timestamp: fixture.timestamp,
      bbox: [139.7349, 35.6897, 139.7361, 35.6908],
      solarAvoidanceFactor: route.profile.solarAvoidanceFactor,
    })
    const byId = new Map(edges.features.map((feature) => [feature.properties.edgeId, feature.properties]))
    const routeEdges = route.edgeIds.map((directedId) => byId.get(directedId.replace(/:(?:forward|backward)$/, '')))
    assert.equal(routeEdges.every(Boolean), true)
    approximately(routeEdges.reduce((total, edge) => total + edge.walkingSeconds, 0), route.kpis.walkingSeconds)
    approximately(routeEdges.reduce((total, edge) => total + edge.assumedSolarExposureSeconds, 0), route.kpis.solarExposureSeconds)
    approximately(routeEdges.reduce((total, edge) => total + edge.routeCostSeconds, 0), route.kpis.routeCostSeconds)
  }
  const shortestEdge = service.roadEdges({
    areaId: 'route-server-fixture', timestamp: fixture.timestamp,
    bbox: [139.7349, 35.6897, 139.7361, 35.6908], solarAvoidanceFactor: 2,
  }).features.find((feature) => feature.properties.edgeId === 'osm-way-201-0')
  assert.equal(shortestEdge.properties.shadeRatio, 0.1)
  assert.equal(shortestEdge.properties.solarExposureSeconds, 90)
  assert.equal(shortestEdge.properties.walkingSeconds, 100)
  assert.equal(shortestEdge.properties.environmentalCostSeconds, 180)
  assert.equal(shortestEdge.properties.routeCostSeconds, 280)
})

test('missing road edges preserve null analysis values and disclose the full-sun assumption', async () => {
  const missingService = await RouteService.load([{ manifestPath: missingManifestPath, maximumSnapDistanceMeters: 100 }])
  const result = missingService.roadEdges({
    areaId: 'ichigaya-integration-fixture',
    timestamp: '2025-08-01T08:00:00+09:00',
    bbox: [139.735, 35.689, 139.738, 35.692],
    solarAvoidanceFactor: 2,
  })
  const missing = result.features.find((feature) => feature.properties.status === 'missing')
  assert.ok(missing)
  assert.equal(missing.properties.shadeRatio, null)
  assert.equal(missing.properties.solarExposureSeconds, null)
  assert.equal(missing.properties.missingCostAssumptionApplied, true)
  assert.equal(missing.properties.assumedSolarExposureSeconds, missing.properties.walkingSeconds)
  assert.equal(missing.properties.routeCostSeconds, missing.properties.walkingSeconds * 3)
  assert.match(missing.properties.missingReason, /未計算|照合|解析値/)
})

test('factor zero exactly follows minimum walking time', () => {
  const custom = service.compare(request({
    profiles: [{ id: 'zero', solarAvoidanceFactor: 0 }],
  }))
  assert.deepEqual(custom.routes[0].edgeIds, ['osm-way-201-0:forward', 'osm-way-202-0:forward'])
  assert.equal(custom.routes[0].kpis.routeCostSeconds, custom.routes[0].kpis.walkingSeconds)
})

test('reverse travel uses backward directed edges', () => {
  const reverse = service.compare(request({ start: fixture.coordinates.end, end: fixture.coordinates.start }))
  assert.deepEqual(reverse.routes[0].edgeIds, ['osm-way-202-0:backward', 'osm-way-201-0:backward'])
})

test('snapping and route failures return stable error codes', () => {
  const exact = service.compare(request({ start: [...fixture.coordinates.start] }))
  assert.equal(exact.snapped.start.distanceMeters, 0)

  assert.throws(
    () => service.compare(request({ timestamp: '2025-08-01T13:00:00+09:00' })),
    (error) => error instanceof RouteError && error.code === 'TIMESTAMP_NOT_AVAILABLE',
  )
  assert.throws(
    () => service.compare(request({ start: [140, 36] })),
    (error) => error instanceof RouteError && error.code === 'OUTSIDE_COVERAGE',
  )
  assert.throws(
    () => service.compare(request({ start: fixture.coordinates.isolated })),
    (error) => error instanceof RouteError && error.code === 'ROUTE_NOT_FOUND',
  )
  assert.throws(
    () => service.compare(request({ profiles: [{ id: 'bad', solarAvoidanceFactor: -1 }] })),
    (error) => error instanceof RouteError && error.code === 'INVALID_PROFILE',
  )
  assert.throws(
    () => service.compare(request({ unexpected: true })),
    (error) => error instanceof RouteError && error.code === 'INVALID_REQUEST',
  )
  assert.throws(
    () => service.compare(request({ profiles: [{ id: 'bad', solarAvoidanceFactor: 1, unexpected: true }] })),
    (error) => error instanceof RouteError && error.code === 'INVALID_PROFILE',
  )
  assert.throws(
    () => service.roadEdges({ areaId: 'route-server-fixture', timestamp: fixture.timestamp, bbox: [139, 35, 140], solarAvoidanceFactor: 2 }),
    (error) => error instanceof RouteError && error.code === 'INVALID_BBOX',
  )
  assert.throws(
    () => service.roadEdges({ areaId: 'route-server-fixture', timestamp: fixture.timestamp, bbox: [139, 35, 140, 36], solarAvoidanceFactor: 2 }),
    (error) => error instanceof RouteError && error.code === 'BBOX_TOO_LARGE',
  )
})
