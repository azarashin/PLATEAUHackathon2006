import assert from 'node:assert/strict'
import test from 'node:test'
import { demoAreas, findCoveredArea, geolocationErrorMessage, haversineMeters, shouldDisplayDataset, V2_ANALYSIS_TIMESTAMPS } from '../src/location-domain.ts'

test('5地域を半径4kmとして定義する', () => {
  assert.deepEqual(demoAreas.map((area) => area.id), ['kyoto', 'maizuru', 'fujisawa', 'saitama', 'ichigaya-venue'])
  assert.ok(demoAreas.every((area) => area.radiusMeters === 4000))
  assert.deepEqual(demoAreas.filter((area) => area.availableTimestamps.length > 0).map((area) => area.id), ['kyoto', 'maizuru', 'fujisawa', 'saitama', 'ichigaya-venue'])
  assert.equal(V2_ANALYSIS_TIMESTAMPS.length, 24)
  assert.equal(V2_ANALYSIS_TIMESTAMPS[0], '2025-08-01T00:00:00+09:00')
  assert.equal(V2_ANALYSIS_TIMESTAMPS.at(-1), '2025-08-01T23:00:00+09:00')
  assert.ok(demoAreas.every((area) => area.availableTimestamps !== V2_ANALYSIS_TIMESTAMPS))
  assert.ok(demoAreas.every((area) => JSON.stringify(area.availableTimestamps) === JSON.stringify(V2_ANALYSIS_TIMESTAMPS)))
})

test('中心点と範囲外のGPS位置を判定する', () => {
  assert.equal(findCoveredArea([139.736043, 35.69047])?.id, 'ichigaya-venue')
  assert.equal(findCoveredArea([141.3545, 43.0621]), undefined)
  assert.ok(haversineMeters([139.736043, 35.69047], [139.736043, 35.69047]) < 0.001)
})

test('GPSエラーを許可拒否・取得不可・タイムアウトで区別する', () => {
  assert.match(geolocationErrorMessage(1), /許可されません/)
  assert.match(geolocationErrorMessage(2), /取得できません/)
  assert.match(geolocationErrorMessage(3), /タイムアウト/)
})

test('選択地域と異なるfixture道路を表示しない', () => {
  assert.equal(shouldDisplayDataset('ichigaya-venue', 'tokyo-demo'), false)
  assert.equal(shouldDisplayDataset('ichigaya-venue', 'ichigaya-venue'), true)
})
