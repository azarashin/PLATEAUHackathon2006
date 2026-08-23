import type { RouteProfileId, RouteResponse } from './route-domain.ts'

export type RoadEdgeStatus = 'available' | 'partial' | 'missing'
export type RoadEdgeCoordinate = [number, number]

export interface RoadEdgeProperties {
  edgeId: string
  status: RoadEdgeStatus
  missingReason: string | null
  sampleCount: number
  validSampleCount: number
  noGroundSampleCount: number
  shadeRatio: number | null
  solarExposureSeconds: number | null
  walkingSeconds: number
  lengthMeters: number
  solarAvoidanceFactor: number
  assumedSolarExposureSeconds: number
  environmentalCostSeconds: number
  routeCostSeconds: number
  missingCostAssumptionApplied: boolean
}

export interface RoadEdgeFeature {
  type: 'Feature'
  id: string
  properties: RoadEdgeProperties
  geometry: { type: 'LineString'; coordinates: RoadEdgeCoordinate[] }
}

export interface RoadEdgeResponse {
  schemaVersion: 'road-edge-response-1.0'
  type: 'FeatureCollection'
  areaId: string
  timestamp: string
  bbox: [number, number, number, number]
  solarAvoidanceFactor: number
  missingCostPolicy: 'assume-fully-sun-and-report-unknown-coverage'
  features: RoadEdgeFeature[]
  diagnostics: { edgeCount: number; queryMilliseconds: number; bundleFingerprintSha256: string }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function finite(value: unknown, label: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) throw new Error(`${label}が不正です。`)
  return value
}

function nonNegative(value: unknown, label: string): number {
  const parsed = finite(value, label)
  if (parsed < 0) throw new Error(`${label}が不正です。`)
  return parsed
}

function nonNegativeInteger(value: unknown, label: string): number {
  const parsed = nonNegative(value, label)
  if (!Number.isInteger(parsed)) throw new Error(`${label}が不正です。`)
  return parsed
}

function nullableRatio(value: unknown): number | null {
  if (value === null) return null
  const parsed = nonNegative(value, '日陰率')
  if (parsed > 1) throw new Error('日陰率が不正です。')
  return parsed
}

function nullableNonNegative(value: unknown, label: string): number | null {
  return value === null ? null : nonNegative(value, label)
}

function coordinate(value: unknown): RoadEdgeCoordinate {
  if (!Array.isArray(value) || value.length !== 2) throw new Error('道路辺の座標が不正です。')
  const longitude = finite(value[0], '経度')
  const latitude = finite(value[1], '緯度')
  if (longitude < -180 || longitude > 180 || latitude < -90 || latitude > 90) throw new Error('道路辺の座標が不正です。')
  return [longitude, latitude]
}

function parseFeature(value: unknown): RoadEdgeFeature {
  if (!isRecord(value) || value.type !== 'Feature' || typeof value.id !== 'string' || !isRecord(value.properties) || !isRecord(value.geometry)) {
    throw new Error('道路辺が不正です。')
  }
  const properties = value.properties
  if (typeof properties.edgeId !== 'string' || properties.edgeId !== value.id) throw new Error('道路辺IDが不正です。')
  if (properties.status !== 'available' && properties.status !== 'partial' && properties.status !== 'missing') throw new Error('道路辺の解析状態が不正です。')
  if (properties.missingReason !== null && typeof properties.missingReason !== 'string') throw new Error('道路辺の欠測理由が不正です。')
  if (typeof properties.missingCostAssumptionApplied !== 'boolean') throw new Error('道路辺の欠測時コスト指定が不正です。')
  if (value.geometry.type !== 'LineString' || !Array.isArray(value.geometry.coordinates) || value.geometry.coordinates.length < 2) throw new Error('道路辺の形状が不正です。')
  const shadeRatio = nullableRatio(properties.shadeRatio)
  const solarExposureSeconds = nullableNonNegative(properties.solarExposureSeconds, '日射曝露時間')
  if (properties.status === 'missing' && (shadeRatio !== null || solarExposureSeconds !== null || !properties.missingCostAssumptionApplied)) {
    throw new Error('欠測道路辺の解析値が不正です。')
  }
  if (properties.status !== 'missing' && (shadeRatio === null || solarExposureSeconds === null || properties.missingCostAssumptionApplied)) {
    throw new Error('解析済み道路辺の値が不正です。')
  }
  return {
    type: 'Feature',
    id: value.id,
    properties: {
      edgeId: properties.edgeId,
      status: properties.status,
      missingReason: properties.missingReason,
      sampleCount: nonNegativeInteger(properties.sampleCount, '解析点数'),
      validSampleCount: nonNegativeInteger(properties.validSampleCount, '有効解析点数'),
      noGroundSampleCount: nonNegativeInteger(properties.noGroundSampleCount, '道路面未照合点数'),
      shadeRatio,
      solarExposureSeconds,
      walkingSeconds: nonNegative(properties.walkingSeconds, '歩行時間'),
      lengthMeters: nonNegative(properties.lengthMeters, '道路辺長'),
      solarAvoidanceFactor: nonNegative(properties.solarAvoidanceFactor, '日射回避係数'),
      assumedSolarExposureSeconds: nonNegative(properties.assumedSolarExposureSeconds, '探索用日射曝露時間'),
      environmentalCostSeconds: nonNegative(properties.environmentalCostSeconds, '環境コスト加算分'),
      routeCostSeconds: nonNegative(properties.routeCostSeconds, '探索コスト'),
      missingCostAssumptionApplied: properties.missingCostAssumptionApplied,
    },
    geometry: { type: 'LineString', coordinates: value.geometry.coordinates.map(coordinate) },
  }
}

export function parseRoadEdgeResponse(value: unknown): RoadEdgeResponse {
  if (!isRecord(value) || value.schemaVersion !== 'road-edge-response-1.0' || value.type !== 'FeatureCollection' || typeof value.areaId !== 'string' || typeof value.timestamp !== 'string') {
    throw new Error('道路辺サーバーの応答形式が不正です。')
  }
  if (!Array.isArray(value.bbox) || value.bbox.length !== 4) throw new Error('道路辺の表示範囲が不正です。')
  const bbox = value.bbox.map((item) => finite(item, '表示範囲')) as [number, number, number, number]
  if (bbox[0] >= bbox[2] || bbox[1] >= bbox[3]) throw new Error('道路辺の表示範囲が不正です。')
  if (value.missingCostPolicy !== 'assume-fully-sun-and-report-unknown-coverage') throw new Error('道路辺の欠測時コスト方針が不正です。')
  if (!Array.isArray(value.features) || !isRecord(value.diagnostics) || typeof value.diagnostics.bundleFingerprintSha256 !== 'string') {
    throw new Error('道路辺サーバーの応答形式が不正です。')
  }
  const features = value.features.map(parseFeature)
  const edgeCount = nonNegativeInteger(value.diagnostics.edgeCount, '道路辺数')
  if (edgeCount !== features.length) throw new Error('道路辺数が一致しません。')
  return {
    schemaVersion: 'road-edge-response-1.0',
    type: 'FeatureCollection',
    areaId: value.areaId,
    timestamp: value.timestamp,
    bbox,
    solarAvoidanceFactor: nonNegative(value.solarAvoidanceFactor, '日射回避係数'),
    missingCostPolicy: value.missingCostPolicy,
    features,
    diagnostics: {
      edgeCount,
      queryMilliseconds: nonNegative(value.diagnostics.queryMilliseconds, '道路辺検索時間'),
      bundleFingerprintSha256: value.diagnostics.bundleFingerprintSha256,
    },
  }
}

export function physicalEdgeId(directedEdgeId: string): string {
  return directedEdgeId.replace(/:(?:forward|backward)$/, '')
}

export function routeProfilesForEdge(edgeId: string, response: RouteResponse | null): RouteProfileId[] {
  if (!response) return []
  return response.routes
    .filter((route) => route.edgeIds.some((directedEdgeId) => physicalEdgeId(directedEdgeId) === edgeId))
    .map((route) => route.profile.id)
}
