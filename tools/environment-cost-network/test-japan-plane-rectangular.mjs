import assert from 'node:assert/strict'
import {
  geographicToPlane,
  geographicToUnityLocal,
  planeToGeographic,
  unityLocalToGeographic,
} from './japan-plane-rectangular.mjs'

const zoneId = 9
const ichigaya = [139.736043, 35.690470]
const tokyoStation = [139.767125, 35.681236]

// Values returned by the Geospatial Information Authority of Japan survey
// calculation API (JGD2011, Japan Plane Rectangular CS IX), 2026-08-23.
const expectedIchigaya = { northingMeters: -34336.4566, eastingMeters: -8805.3267 }
const expectedTokyoStation = { northingMeters: -35363.2377, eastingMeters: -5992.9196 }

for (const [coordinate, expected] of [[ichigaya, expectedIchigaya], [tokyoStation, expectedTokyoStation]]) {
  const projected = geographicToPlane(coordinate, zoneId)
  assert.ok(Math.abs(projected.northingMeters - expected.northingMeters) < 0.001, `northing mismatch: ${JSON.stringify(projected)}`)
  assert.ok(Math.abs(projected.eastingMeters - expected.eastingMeters) < 0.001, `easting mismatch: ${JSON.stringify(projected)}`)
  const restored = planeToGeographic(expected, zoneId)
  assert.ok(Math.abs(restored[0] - coordinate[0]) < 1e-8)
  assert.ok(Math.abs(restored[1] - coordinate[1]) < 1e-8)
}

const unityLocal = geographicToUnityLocal(tokyoStation, ichigaya, zoneId)
assert.ok(Math.abs(unityLocal[0] - 2812.4071) < 0.002)
assert.equal(unityLocal[1], 0)
assert.ok(Math.abs(unityLocal[2] - -1026.7811) < 0.002)
const restoredTokyoStation = unityLocalToGeographic(unityLocal, ichigaya, zoneId)
assert.ok(Math.abs(restoredTokyoStation[0] - tokyoStation[0]) < 1e-9)
assert.ok(Math.abs(restoredTokyoStation[1] - tokyoStation[1]) < 1e-9)

console.log('JAPAN_PLANE_RECTANGULAR_TEST_PASSED')
