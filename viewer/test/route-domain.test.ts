import assert from 'node:assert/strict'
import test from 'node:test'
import {
  comparisonSummary,
  formatDistance,
  formatDuration,
  formatShadeRatio,
  identicalRouteGroups,
  parseRouteResponse,
  profilesForShadeFactor,
} from '../src/route-domain.ts'

function responseDocument() {
  const route = (id: 'shortest' | 'balanced' | 'shade', walkingSeconds: number, solarExposureSeconds: number, edgeIds: string[]) => ({
    profile: { id, solarAvoidanceFactor: id === 'shortest' ? 0 : id === 'balanced' ? 0.5 : 2 },
    edgeIds,
    geometry: { type: 'LineString', coordinates: [[139.73, 35.69], [139.74, 35.70]] },
    kpis: {
      distanceMeters: walkingSeconds * 1.4,
      walkingSeconds,
      solarExposureSeconds,
      observedSolarExposureSeconds: solarExposureSeconds,
      unknownWalkingSeconds: 0,
      shadeRatio: 1 - solarExposureSeconds / walkingSeconds,
      observedShadeRatio: 1 - solarExposureSeconds / walkingSeconds,
      routeCostSeconds: walkingSeconds + solarExposureSeconds,
      edgeCount: edgeIds.length,
      missingEdgeCount: 0,
      partialEdgeCount: 0,
      coverageStatus: 'available',
    },
  })
  return {
    schemaVersion: 'route-response-1.0',
    areaId: 'ichigaya-venue',
    timestamp: '2025-08-01T12:00:00+09:00',
    presentation: { kpiLabels: { unknownWalkingSeconds: '不明な歩行時間' } },
    snapped: {
      start: { snappedCoordinate: [139.73, 35.69], distanceMeters: 4.2 },
      end: { snappedCoordinate: [139.74, 35.70], distanceMeters: 5.1 },
    },
    routes: [
      route('shortest', 200, 180, ['a', 'b']),
      route('balanced', 230, 115, ['c', 'd']),
      route('shade', 300, 15, ['c', 'd']),
    ],
  }
}

test('正式な3経路応答を解析して未丸めKPIを保持する', () => {
  const parsed = parseRouteResponse(responseDocument())
  assert.equal(parsed.routes.length, 3)
  assert.equal(parsed.routes[1]?.kpis.walkingSeconds, 230)
  assert.equal(parsed.presentation.kpiLabels.unknownWalkingSeconds, '不明な歩行時間')
})

test('日陰優先度から既定3プロファイルを生成する', () => {
  assert.deepEqual(profilesForShadeFactor(2), [
    { id: 'shortest', solarAvoidanceFactor: 0 },
    { id: 'balanced', solarAvoidanceFactor: 0.5 },
    { id: 'shade', solarAvoidanceFactor: 2 },
  ])
  assert.equal(profilesForShadeFactor(200)[2]?.solarAvoidanceFactor, 100)
})

test('正式仕様の表示単位でだけ丸める', () => {
  assert.equal(formatDistance(1234.49), '1,234 m')
  assert.equal(formatDuration(125.6), '2分6秒')
  assert.equal(formatShadeRatio(0.754), '75%')
  assert.equal(formatShadeRatio(null), '不明')
})

test('追加時間と日向削減、および同一路線を説明する', () => {
  const parsed = parseRouteResponse(responseDocument())
  assert.equal(comparisonSummary(parsed.routes[1]!, parsed.routes[0]!), '追加30秒で日向時間を1分5秒削減')
  assert.deepEqual(identicalRouteGroups(parsed.routes), [['balanced', 'shade']])
})

test('3プロファイルが揃わない応答を拒否する', () => {
  const document = responseDocument()
  document.routes.pop()
  assert.throws(() => parseRouteResponse(document), /3経路/)
})
