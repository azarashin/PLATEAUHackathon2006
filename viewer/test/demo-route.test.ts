import assert from 'node:assert/strict'
import test from 'node:test'
import { ICHIGAYA_DEMO_ROUTE } from '../src/demo-route.ts'
import { routeErrorMessage } from '../src/route-error-domain.ts'

test('市ヶ谷デモ条件は実バンドルの固定検証条件を保持する', () => {
  assert.deepEqual(ICHIGAYA_DEMO_ROUTE, {
    areaId: 'ichigaya-venue',
    timestamp: '2025-08-01T12:00:00+09:00',
    start: [139.736043, 35.69047],
    end: [139.700556, 35.689606],
    shadeFactor: 2,
  })
})

test('主要な経路API異常は次の操作を案内する', () => {
  assert.match(routeErrorMessage('SNAP_NOT_FOUND'), /道路付近を選び直/)
  assert.match(routeErrorMessage('OUTSIDE_COVERAGE'), /対象範囲内/)
  assert.match(routeErrorMessage('TIMESTAMP_NOT_AVAILABLE'), /日時を選び直/)
  assert.match(routeErrorMessage('ROUTE_NOT_FOUND'), /地点を選び直/)
  assert.match(routeErrorMessage('UNKNOWN'), /時間をおいて/)
})
