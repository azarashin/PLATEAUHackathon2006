import type { Coordinate } from './location-domain.ts'

export interface DemoRoutePreset {
  areaId: string
  timestamp: string
  start: Coordinate
  end: Coordinate
  shadeFactor: number
}

// 市ヶ谷の実バンドルを検証する固定条件。server/scripts/verify-ichigaya-route.mjsと同期する。
export const ICHIGAYA_DEMO_ROUTE: Readonly<DemoRoutePreset> = Object.freeze({
  areaId: 'ichigaya-venue',
  timestamp: '2025-08-01T12:00:00+09:00',
  start: [139.736043, 35.69047] as Coordinate,
  end: [139.700556, 35.689606] as Coordinate,
  shadeFactor: 2,
})
