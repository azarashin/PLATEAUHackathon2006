import assert from 'node:assert/strict'
import test from 'node:test'
import { parseRoadEdgeResponse, physicalEdgeId, routeProfilesForEdge } from '../src/road-edge-domain.ts'
import type { RouteResponse } from '../src/route-domain.ts'

function response(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: 'road-edge-response-1.0',
    type: 'FeatureCollection',
    areaId: 'ichigaya-venue',
    timestamp: '2025-08-01T12:00:00+09:00',
    bbox: [139.73, 35.68, 139.74, 35.7],
    solarAvoidanceFactor: 2,
    missingCostPolicy: 'assume-fully-sun-and-report-unknown-coverage',
    features: [{
      type: 'Feature', id: 'osm-way-1-0',
      properties: {
        edgeId: 'osm-way-1-0', status: 'available', missingReason: null,
        sampleCount: 4, validSampleCount: 4, noGroundSampleCount: 0,
        shadeRatio: 0.75, solarExposureSeconds: 25, walkingSeconds: 100, lengthMeters: 140,
        solarAvoidanceFactor: 2, assumedSolarExposureSeconds: 25,
        environmentalCostSeconds: 50, routeCostSeconds: 150, missingCostAssumptionApplied: false,
      },
      geometry: { type: 'LineString', coordinates: [[139.735, 35.69], [139.736, 35.691]] },
    }],
    diagnostics: { edgeCount: 1, queryMilliseconds: 2.5, bundleFingerprintSha256: 'a'.repeat(64) },
    ...overrides,
  }
}

test('parses analyzed and missing road edge evidence without converting missing to shade', () => {
  const parsed = parseRoadEdgeResponse(response())
  assert.equal(parsed.features[0].properties.shadeRatio, 0.75)
  const missingDocument = response({
    features: [{
      ...response().features[0],
      properties: {
        ...response().features[0].properties,
        status: 'missing', missingReason: '未計算です。', shadeRatio: null, solarExposureSeconds: null,
        assumedSolarExposureSeconds: 100, environmentalCostSeconds: 200, routeCostSeconds: 300,
        missingCostAssumptionApplied: true,
      },
    }],
  })
  const missing = parseRoadEdgeResponse(missingDocument).features[0]
  assert.equal(missing.properties.shadeRatio, null)
  assert.equal(missing.properties.missingCostAssumptionApplied, true)
  assert.throws(() => parseRoadEdgeResponse(response({ features: missingDocument.features.map((feature: any) => ({ ...feature, properties: { ...feature.properties, shadeRatio: 0 } })) })), /欠測道路辺/)
})

test('matches directed route edge IDs to physical road edge IDs', () => {
  assert.equal(physicalEdgeId('osm-way-1-0:forward'), 'osm-way-1-0')
  const routeResponse = {
    routes: [
      { profile: { id: 'shortest' }, edgeIds: ['osm-way-1-0:forward'] },
      { profile: { id: 'balanced' }, edgeIds: ['osm-way-2-0:forward'] },
      { profile: { id: 'shade' }, edgeIds: ['osm-way-1-0:backward'] },
    ],
  } as RouteResponse
  assert.deepEqual(routeProfilesForEdge('osm-way-1-0', routeResponse), ['shortest', 'shade'])
})
