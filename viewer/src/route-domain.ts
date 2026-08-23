export type RouteCoordinate = [number, number]
export type RouteProfileId = 'shortest' | 'balanced' | 'shade'

export interface RouteProfile {
  id: RouteProfileId
  solarAvoidanceFactor: number
}

export interface RouteKpis {
  distanceMeters: number
  walkingSeconds: number
  solarExposureSeconds: number
  observedSolarExposureSeconds: number
  unknownWalkingSeconds: number
  shadeRatio: number
  observedShadeRatio: number | null
  routeCostSeconds: number
  edgeCount: number
  missingEdgeCount: number
  partialEdgeCount: number
  coverageStatus: 'available' | 'partial' | 'missing'
}

export interface CalculatedRoute {
  profile: RouteProfile
  edgeIds: string[]
  geometry: { type: 'LineString'; coordinates: RouteCoordinate[] }
  kpis: RouteKpis
}

export interface RouteResponse {
  schemaVersion: 'route-response-1.0'
  areaId: string
  timestamp: string
  presentation: { kpiLabels: { unknownWalkingSeconds: string } }
  snapped: {
    start: { snappedCoordinate: RouteCoordinate; distanceMeters: number }
    end: { snappedCoordinate: RouteCoordinate; distanceMeters: number }
  }
  routes: CalculatedRoute[]
}

export const DEFAULT_SHADE_FACTOR = 2
export const PROFILE_ORDER: RouteProfileId[] = ['shortest', 'balanced', 'shade']

const PROFILE_IDS = new Set<RouteProfileId>(PROFILE_ORDER)

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

function coordinate(value: unknown, label: string): RouteCoordinate {
  if (!Array.isArray(value) || value.length !== 2 || !isFiniteNumber(value[0]) || !isFiniteNumber(value[1])) {
    throw new Error(`${label}の座標が不正です。`)
  }
  return [value[0], value[1]]
}

function nonNegative(value: unknown, label: string): number {
  if (!isFiniteNumber(value) || value < 0) throw new Error(`${label}が不正です。`)
  return value
}

function nonNegativeInteger(value: unknown, label: string): number {
  const parsed = nonNegative(value, label)
  if (!Number.isInteger(parsed)) throw new Error(`${label}が不正です。`)
  return parsed
}

function parseKpis(value: unknown): RouteKpis {
  if (!isRecord(value)) throw new Error('経路KPIが不正です。')
  const shadeRatio = nonNegative(value.shadeRatio, '日陰率')
  if (shadeRatio > 1) throw new Error('日陰率が不正です。')
  const observedShadeRatio = value.observedShadeRatio === null ? null : nonNegative(value.observedShadeRatio, '観測済み日陰率')
  if (observedShadeRatio !== null && observedShadeRatio > 1) throw new Error('観測済み日陰率が不正です。')
  if (value.coverageStatus !== 'available' && value.coverageStatus !== 'partial' && value.coverageStatus !== 'missing') {
    throw new Error('経路のデータ充足状態が不正です。')
  }
  return {
    distanceMeters: nonNegative(value.distanceMeters, '距離'),
    walkingSeconds: nonNegative(value.walkingSeconds, '歩行時間'),
    solarExposureSeconds: nonNegative(value.solarExposureSeconds, '日向時間'),
    observedSolarExposureSeconds: nonNegative(value.observedSolarExposureSeconds, '観測済み日向時間'),
    unknownWalkingSeconds: nonNegative(value.unknownWalkingSeconds, '不明な歩行時間'),
    shadeRatio,
    observedShadeRatio,
    routeCostSeconds: nonNegative(value.routeCostSeconds, '探索コスト'),
    edgeCount: nonNegativeInteger(value.edgeCount, '辺数'),
    missingEdgeCount: nonNegativeInteger(value.missingEdgeCount, '欠測辺数'),
    partialEdgeCount: nonNegativeInteger(value.partialEdgeCount, '部分欠測辺数'),
    coverageStatus: value.coverageStatus,
  }
}

function parseRoute(value: unknown): CalculatedRoute {
  if (!isRecord(value) || !isRecord(value.profile) || !isRecord(value.geometry)) throw new Error('経路情報が不正です。')
  const id = value.profile.id
  if (typeof id !== 'string' || !PROFILE_IDS.has(id as RouteProfileId)) throw new Error('経路プロファイルが不正です。')
  const solarAvoidanceFactor = nonNegative(value.profile.solarAvoidanceFactor, '日向回避係数')
  if (!Array.isArray(value.edgeIds) || !value.edgeIds.every((edgeId) => typeof edgeId === 'string')) throw new Error('経路の辺IDが不正です。')
  if (value.geometry.type !== 'LineString' || !Array.isArray(value.geometry.coordinates) || value.geometry.coordinates.length < 2) {
    throw new Error('経路形状が不正です。')
  }
  return {
    profile: { id: id as RouteProfileId, solarAvoidanceFactor },
    edgeIds: [...value.edgeIds],
    geometry: { type: 'LineString', coordinates: value.geometry.coordinates.map((item) => coordinate(item, '経路')) },
    kpis: parseKpis(value.kpis),
  }
}

export function parseRouteResponse(value: unknown): RouteResponse {
  if (!isRecord(value) || value.schemaVersion !== 'route-response-1.0' || typeof value.areaId !== 'string' || typeof value.timestamp !== 'string') {
    throw new Error('経路サーバーの応答形式が不正です。')
  }
  if (!isRecord(value.presentation) || !isRecord(value.presentation.kpiLabels) || typeof value.presentation.kpiLabels.unknownWalkingSeconds !== 'string') {
    throw new Error('KPI表示情報が不正です。')
  }
  if (!isRecord(value.snapped) || !isRecord(value.snapped.start) || !isRecord(value.snapped.end)) throw new Error('スナップ結果が不正です。')
  if (!Array.isArray(value.routes) || value.routes.length !== 3) throw new Error('3経路の応答が必要です。')
  const routes = value.routes.map(parseRoute)
  const ids = new Set(routes.map((route) => route.profile.id))
  if (ids.size !== PROFILE_ORDER.length || PROFILE_ORDER.some((id) => !ids.has(id))) throw new Error('必要な経路プロファイルが揃っていません。')
  return {
    schemaVersion: 'route-response-1.0',
    areaId: value.areaId,
    timestamp: value.timestamp,
    presentation: { kpiLabels: { unknownWalkingSeconds: value.presentation.kpiLabels.unknownWalkingSeconds } },
    snapped: {
      start: { snappedCoordinate: coordinate(value.snapped.start.snappedCoordinate, '出発地'), distanceMeters: nonNegative(value.snapped.start.distanceMeters, '出発地のスナップ距離') },
      end: { snappedCoordinate: coordinate(value.snapped.end.snappedCoordinate, '目的地'), distanceMeters: nonNegative(value.snapped.end.distanceMeters, '目的地のスナップ距離') },
    },
    routes,
  }
}

export function profilesForShadeFactor(value: number): RouteProfile[] {
  const shadeFactor = Math.min(100, Math.max(0, value))
  return [
    { id: 'shortest', solarAvoidanceFactor: 0 },
    { id: 'balanced', solarAvoidanceFactor: shadeFactor / 4 },
    { id: 'shade', solarAvoidanceFactor: shadeFactor },
  ]
}

export function routesInDisplayOrder(routes: CalculatedRoute[]): CalculatedRoute[] {
  return PROFILE_ORDER.map((id) => routes.find((route) => route.profile.id === id)).filter((route): route is CalculatedRoute => route !== undefined)
}

export function identicalRouteGroups(routes: CalculatedRoute[]): RouteProfileId[][] {
  const groups = new Map<string, RouteProfileId[]>()
  for (const route of routes) {
    const fingerprint = route.edgeIds.join('\u0000')
    const group = groups.get(fingerprint) ?? []
    group.push(route.profile.id)
    groups.set(fingerprint, group)
  }
  return [...groups.values()].filter((group) => group.length > 1)
}

export function formatDistance(meters: number): string {
  return `${Math.round(meters).toLocaleString('ja-JP')} m`
}

export function formatDuration(seconds: number): string {
  const rounded = Math.round(seconds)
  const minutes = Math.floor(rounded / 60)
  const remainder = rounded % 60
  return minutes === 0 ? `${remainder}秒` : remainder === 0 ? `${minutes}分` : `${minutes}分${remainder}秒`
}

export function formatShadeRatio(value: number | null): string {
  return value === null ? '不明' : `${Math.round(value * 100)}%`
}

export function comparisonSummary(route: CalculatedRoute, shortest: CalculatedRoute): string {
  if (route.profile.id === 'shortest') return '他の経路を比較する基準です。'
  const additionalWalking = Math.max(0, route.kpis.walkingSeconds - shortest.kpis.walkingSeconds)
  const reducedExposure = Math.max(0, shortest.kpis.solarExposureSeconds - route.kpis.solarExposureSeconds)
  return `追加${formatDuration(additionalWalking)}で日向時間を${formatDuration(reducedExposure)}削減`
}
