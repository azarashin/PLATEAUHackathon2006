import { performance } from 'node:perf_hooks'
import { RouteError, invariantRoute } from './route-error.mjs'

const DEFAULT_CELL_SIZE_DEGREES = 0.005
const DEFAULT_MAXIMUM_FEATURES = 10_000

function cellCoordinate(value, size) {
  return Math.floor(value / size)
}

function cellKey(x, y) {
  return `${x}:${y}`
}

function intersects(edge, bbox) {
  return edge.maximumLongitude >= bbox[0]
    && edge.minimumLongitude <= bbox[2]
    && edge.maximumLatitude >= bbox[1]
    && edge.minimumLatitude <= bbox[3]
}

function missingReason(cost) {
  if (cost.status === 'partial') {
    return cost.noGroundSampleCount > 0
      ? '一部の解析点で道路面を照合できませんでした。'
      : '一部の解析点が欠測です。'
  }
  if (cost.status !== 'missing') return null
  if (cost.sampleCount === 0) return '解析対象の道路面サンプルがなく、未計算です。'
  if (cost.validSampleCount === 0 && cost.noGroundSampleCount > 0) return '道路面を照合できませんでした。'
  return 'この道路辺の解析値がありません。'
}

function featureForEdge(runtime, edge, timestamp, solarAvoidanceFactor) {
  const cost = runtime.directedEdgeCost(edge.directedEdgeIndex, timestamp)
  const missingCostAssumptionApplied = cost.status === 'missing'
  const assumedSolarExposureSeconds = missingCostAssumptionApplied ? edge.walkingSeconds : cost.solarExposureSeconds
  invariantRoute(Number.isFinite(assumedSolarExposureSeconds) && assumedSolarExposureSeconds >= 0, 'INVALID_EDGE_COST', 'The loaded edge cost is invalid.', 500)
  const environmentalCostSeconds = assumedSolarExposureSeconds * solarAvoidanceFactor
  return {
    type: 'Feature',
    id: edge.id,
    properties: {
      edgeId: edge.id,
      status: cost.status,
      missingReason: missingReason(cost),
      sampleCount: cost.sampleCount,
      validSampleCount: cost.validSampleCount,
      noGroundSampleCount: cost.noGroundSampleCount,
      shadeRatio: cost.shadeRatio,
      solarExposureSeconds: cost.solarExposureSeconds,
      walkingSeconds: edge.walkingSeconds,
      lengthMeters: edge.lengthMeters,
      solarAvoidanceFactor,
      assumedSolarExposureSeconds,
      environmentalCostSeconds,
      routeCostSeconds: edge.walkingSeconds + environmentalCostSeconds,
      missingCostAssumptionApplied,
    },
    geometry: {
      type: 'LineString',
      coordinates: edge.geometry,
    },
  }
}

export class RoadEdgeIndex {
  constructor(runtime, options = {}) {
    this.runtime = runtime
    this.cellSizeDegrees = options.cellSizeDegrees ?? DEFAULT_CELL_SIZE_DEGREES
    this.maximumFeatures = options.maximumFeatures ?? DEFAULT_MAXIMUM_FEATURES
    invariantRoute(Number.isFinite(this.cellSizeDegrees) && this.cellSizeDegrees > 0, 'INVALID_SERVER_CONFIG', 'road edge cell size must be positive.', 500)
    invariantRoute(Number.isInteger(this.maximumFeatures) && this.maximumFeatures > 0, 'INVALID_SERVER_CONFIG', 'maximum road edge features must be a positive integer.', 500)
    this.edges = new Array(runtime.physicalEdgeIds.length)
    this.cells = new Map()

    for (let directedEdgeIndex = 0; directedEdgeIndex < runtime.directedPhysicalIndexes.length; directedEdgeIndex += 1) {
      const physicalIndex = runtime.directedPhysicalIndexes[directedEdgeIndex]
      if (this.edges[physicalIndex] !== undefined) continue
      const geometry = runtime.directedEdgeGeometry(directedEdgeIndex)
      const minimumLongitude = Math.min(...geometry.map((point) => point[0]))
      const minimumLatitude = Math.min(...geometry.map((point) => point[1]))
      const maximumLongitude = Math.max(...geometry.map((point) => point[0]))
      const maximumLatitude = Math.max(...geometry.map((point) => point[1]))
      this.edges[physicalIndex] = {
        id: runtime.physicalEdgeIds[physicalIndex],
        directedEdgeIndex,
        geometry,
        walkingSeconds: runtime.directedWalkingSeconds[directedEdgeIndex],
        lengthMeters: runtime.directedLengthMeters[directedEdgeIndex],
        minimumLongitude,
        minimumLatitude,
        maximumLongitude,
        maximumLatitude,
      }
    }
    invariantRoute(this.edges.every(Boolean), 'INVALID_SERVER_CONFIG', 'Every physical edge must have a directed edge.', 500)

    for (let physicalIndex = 0; physicalIndex < this.edges.length; physicalIndex += 1) {
      const edge = this.edges[physicalIndex]
      const minimumX = cellCoordinate(edge.minimumLongitude, this.cellSizeDegrees)
      const maximumX = cellCoordinate(edge.maximumLongitude, this.cellSizeDegrees)
      const minimumY = cellCoordinate(edge.minimumLatitude, this.cellSizeDegrees)
      const maximumY = cellCoordinate(edge.maximumLatitude, this.cellSizeDegrees)
      for (let x = minimumX; x <= maximumX; x += 1) {
        for (let y = minimumY; y <= maximumY; y += 1) {
          const key = cellKey(x, y)
          const indexes = this.cells.get(key) ?? []
          indexes.push(physicalIndex)
          this.cells.set(key, indexes)
        }
      }
    }
  }

  query(bbox, timestamp, solarAvoidanceFactor) {
    invariantRoute(this.runtime.costsByTimestamp.has(timestamp), 'TIMESTAMP_NOT_AVAILABLE', 'The requested timestamp is not loaded.', 422, { availableTimestamps: [...this.runtime.costsByTimestamp.keys()] })
    const started = performance.now()
    const minimumX = cellCoordinate(bbox[0], this.cellSizeDegrees)
    const maximumX = cellCoordinate(bbox[2], this.cellSizeDegrees)
    const minimumY = cellCoordinate(bbox[1], this.cellSizeDegrees)
    const maximumY = cellCoordinate(bbox[3], this.cellSizeDegrees)
    const candidates = new Set()
    for (let x = minimumX; x <= maximumX; x += 1) {
      for (let y = minimumY; y <= maximumY; y += 1) {
        for (const physicalIndex of this.cells.get(cellKey(x, y)) ?? []) candidates.add(physicalIndex)
      }
    }
    const matches = [...candidates].filter((physicalIndex) => intersects(this.edges[physicalIndex], bbox)).sort((left, right) => left - right)
    if (matches.length > this.maximumFeatures) {
      throw new RouteError('TOO_MANY_ROAD_EDGES', 'The requested map extent contains too many road edges. Zoom in and retry.', 422, {
        matchedEdgeCount: matches.length,
        maximumEdgeCount: this.maximumFeatures,
      })
    }
    return {
      features: matches.map((physicalIndex) => featureForEdge(this.runtime, this.edges[physicalIndex], timestamp, solarAvoidanceFactor)),
      queryMilliseconds: performance.now() - started,
    }
  }
}
