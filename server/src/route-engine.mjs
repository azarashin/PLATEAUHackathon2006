import { performance } from 'node:perf_hooks'
import { MinPriorityQueue } from './priority-queue.mjs'
import { RouteError, invariantRoute } from './route-error.mjs'

const EARTH_RADIUS_METERS = 6371008.8
const WEIGHT_TOLERANCE = 1e-9

export const DEFAULT_PROFILES = Object.freeze([
  Object.freeze({ id: 'shortest', solarAvoidanceFactor: 0 }),
  Object.freeze({ id: 'balanced', solarAvoidanceFactor: 0.5 }),
  Object.freeze({ id: 'shade', solarAvoidanceFactor: 2 }),
])

function degreesToRadians(value) {
  return value * Math.PI / 180
}

export function haversineMeters(left, right) {
  const latitudeDelta = degreesToRadians(right[1] - left[1])
  const longitudeDelta = degreesToRadians(right[0] - left[0])
  const leftLatitude = degreesToRadians(left[1])
  const rightLatitude = degreesToRadians(right[1])
  const haversine = Math.sin(latitudeDelta / 2) ** 2
    + Math.cos(leftLatitude) * Math.cos(rightLatitude) * Math.sin(longitudeDelta / 2) ** 2
  return 2 * EARTH_RADIUS_METERS * Math.asin(Math.min(1, Math.sqrt(haversine)))
}

function buildOutgoingIndex(runtime) {
  const nodeCount = runtime.nodeSourceIds.length
  const edgeCount = runtime.directedFromNodeIndexes.length
  const offsets = new Uint32Array(nodeCount + 1)
  for (let edgeIndex = 0; edgeIndex < edgeCount; edgeIndex += 1) offsets[runtime.directedFromNodeIndexes[edgeIndex] + 1] += 1
  for (let nodeIndex = 1; nodeIndex < offsets.length; nodeIndex += 1) offsets[nodeIndex] += offsets[nodeIndex - 1]
  const cursor = offsets.slice(0, nodeCount)
  const edgeIndexes = new Uint32Array(edgeCount)
  for (let edgeIndex = 0; edgeIndex < edgeCount; edgeIndex += 1) {
    const from = runtime.directedFromNodeIndexes[edgeIndex]
    edgeIndexes[cursor[from]] = edgeIndex
    cursor[from] += 1
  }
  return { offsets, edgeIndexes }
}

function coordinateAt(runtime, nodeIndex) {
  return [runtime.nodeLongitudes[nodeIndex], runtime.nodeLatitudes[nodeIndex]]
}

function effectiveEdgeCost(runtime, edgeIndex, timestamp, solarAvoidanceFactor) {
  const walkingSeconds = runtime.directedWalkingSeconds[edgeIndex]
  const cost = runtime.directedEdgeCost(edgeIndex, timestamp)
  const missing = cost.status === 'missing'
  const solarExposureSeconds = missing ? walkingSeconds : cost.solarExposureSeconds
  invariantRoute(Number.isFinite(solarExposureSeconds) && solarExposureSeconds >= 0, 'INVALID_EDGE_COST', 'The loaded edge cost is invalid.', 500)
  return {
    weight: walkingSeconds + solarExposureSeconds * solarAvoidanceFactor,
    walkingSeconds,
    solarExposureSeconds,
    observedSolarExposureSeconds: missing ? 0 : solarExposureSeconds,
    knownWalkingSeconds: missing ? 0 : walkingSeconds,
    missing,
    partial: cost.status === 'partial',
  }
}

function routeGeometry(runtime, edgeIndexes, fallbackNodeIndex) {
  if (edgeIndexes.length === 0) {
    const coordinate = coordinateAt(runtime, fallbackNodeIndex)
    return [coordinate, [...coordinate]]
  }
  const coordinates = []
  for (const edgeIndex of edgeIndexes) {
    const segment = runtime.directedEdgeGeometry(edgeIndex)
    if (coordinates.length === 0) coordinates.push(segment[0])
    coordinates.push(segment[1])
  }
  return coordinates
}

function summarizeRoute(runtime, edgeIndexes, timestamp, solarAvoidanceFactor) {
  let distanceMeters = 0
  let walkingSeconds = 0
  let solarExposureSeconds = 0
  let observedSolarExposureSeconds = 0
  let knownWalkingSeconds = 0
  let missingEdgeCount = 0
  let partialEdgeCount = 0
  for (const edgeIndex of edgeIndexes) {
    const effective = effectiveEdgeCost(runtime, edgeIndex, timestamp, solarAvoidanceFactor)
    distanceMeters += runtime.directedLengthMeters[edgeIndex]
    walkingSeconds += effective.walkingSeconds
    solarExposureSeconds += effective.solarExposureSeconds
    observedSolarExposureSeconds += effective.observedSolarExposureSeconds
    knownWalkingSeconds += effective.knownWalkingSeconds
    if (effective.missing) missingEdgeCount += 1
    if (effective.partial) partialEdgeCount += 1
  }
  const shadeRatio = walkingSeconds === 0 ? 0 : 1 - solarExposureSeconds / walkingSeconds
  const observedShadeRatio = knownWalkingSeconds === 0 ? null : 1 - observedSolarExposureSeconds / knownWalkingSeconds
  return {
    distanceMeters,
    walkingSeconds,
    solarExposureSeconds,
    observedSolarExposureSeconds,
    unknownWalkingSeconds: walkingSeconds - knownWalkingSeconds,
    shadeRatio,
    observedShadeRatio,
    routeCostSeconds: walkingSeconds + solarExposureSeconds * solarAvoidanceFactor,
    edgeCount: edgeIndexes.length,
    missingEdgeCount,
    partialEdgeCount,
    coverageStatus: missingEdgeCount > 0 ? 'missing' : partialEdgeCount > 0 ? 'partial' : 'available',
  }
}

export class RouteEngine {
  constructor(runtime, options = {}) {
    this.runtime = runtime
    this.maximumSnapDistanceMeters = options.maximumSnapDistanceMeters ?? 250
    invariantRoute(Number.isFinite(this.maximumSnapDistanceMeters) && this.maximumSnapDistanceMeters > 0, 'INVALID_SERVER_CONFIG', 'maximumSnapDistanceMeters must be positive.', 500)
    const outgoing = buildOutgoingIndex(runtime)
    this.outgoingOffsets = outgoing.offsets
    this.outgoingEdgeIndexes = outgoing.edgeIndexes
  }

  snap(coordinate) {
    invariantRoute(Array.isArray(coordinate) && coordinate.length === 2, 'INVALID_COORDINATE', 'A coordinate must be [longitude, latitude].', 400)
    invariantRoute(Number.isFinite(coordinate[0]) && coordinate[0] >= -180 && coordinate[0] <= 180 && Number.isFinite(coordinate[1]) && coordinate[1] >= -90 && coordinate[1] <= 90, 'INVALID_COORDINATE', 'The coordinate is outside the WGS84 range.', 400)
    const areaCenter = this.runtime.manifest.area.center
    const distanceFromCenterMeters = haversineMeters(coordinate, areaCenter)
    invariantRoute(distanceFromCenterMeters <= this.runtime.manifest.area.radiusMeters + this.maximumSnapDistanceMeters, 'OUTSIDE_COVERAGE', 'The coordinate is outside the precomputed area.', 422, { distanceFromCenterMeters })
    let nearestIndex = -1
    let nearestDistance = Number.POSITIVE_INFINITY
    for (let nodeIndex = 0; nodeIndex < this.runtime.nodeSourceIds.length; nodeIndex += 1) {
      const distance = haversineMeters(coordinate, coordinateAt(this.runtime, nodeIndex))
      if (distance < nearestDistance - WEIGHT_TOLERANCE || (Math.abs(distance - nearestDistance) <= WEIGHT_TOLERANCE && nodeIndex < nearestIndex)) {
        nearestIndex = nodeIndex
        nearestDistance = distance
      }
    }
    invariantRoute(nearestIndex >= 0 && nearestDistance <= this.maximumSnapDistanceMeters, 'SNAP_NOT_FOUND', 'No walkable node is within the snapping distance.', 422, { maximumSnapDistanceMeters: this.maximumSnapDistanceMeters, nearestDistanceMeters: nearestDistance })
    return {
      nodeIndex: nearestIndex,
      nodeId: this.runtime.nodeId(nearestIndex),
      inputCoordinate: [...coordinate],
      snappedCoordinate: coordinateAt(this.runtime, nearestIndex),
      distanceMeters: nearestDistance,
    }
  }

  route(startNodeIndex, endNodeIndex, timestamp, profile) {
    invariantRoute(this.runtime.costsByTimestamp.has(timestamp), 'TIMESTAMP_NOT_AVAILABLE', 'The requested timestamp is not loaded.', 422, { availableTimestamps: [...this.runtime.costsByTimestamp.keys()] })
    invariantRoute(Number.isInteger(startNodeIndex) && startNodeIndex >= 0 && startNodeIndex < this.runtime.nodeSourceIds.length, 'INVALID_START_NODE', 'The start node is invalid.', 400)
    invariantRoute(Number.isInteger(endNodeIndex) && endNodeIndex >= 0 && endNodeIndex < this.runtime.nodeSourceIds.length, 'INVALID_END_NODE', 'The end node is invalid.', 400)
    invariantRoute(profile && typeof profile.id === 'string' && Number.isFinite(profile.solarAvoidanceFactor) && profile.solarAvoidanceFactor >= 0, 'INVALID_PROFILE', 'The route profile is invalid.', 400)
    const started = performance.now()
    const nodeCount = this.runtime.nodeSourceIds.length
    const distances = new Float64Array(nodeCount)
    distances.fill(Number.POSITIVE_INFINITY)
    const previousEdges = new Int32Array(nodeCount)
    previousEdges.fill(-1)
    const visited = new Uint8Array(nodeCount)
    const queue = new MinPriorityQueue()
    distances[startNodeIndex] = 0
    queue.push(startNodeIndex, 0)
    let visitedNodeCount = 0

    while (queue.size > 0) {
      const current = queue.pop()
      if (current.priority > distances[current.node] + WEIGHT_TOLERANCE || visited[current.node]) continue
      visited[current.node] = 1
      visitedNodeCount += 1
      if (current.node === endNodeIndex) break
      const begin = this.outgoingOffsets[current.node]
      const end = this.outgoingOffsets[current.node + 1]
      for (let offset = begin; offset < end; offset += 1) {
        const edgeIndex = this.outgoingEdgeIndexes[offset]
        const toNode = this.runtime.directedToNodeIndexes[edgeIndex]
        if (visited[toNode]) continue
        const edge = effectiveEdgeCost(this.runtime, edgeIndex, timestamp, profile.solarAvoidanceFactor)
        const candidate = distances[current.node] + edge.weight
        if (candidate < distances[toNode] - WEIGHT_TOLERANCE) {
          distances[toNode] = candidate
          previousEdges[toNode] = edgeIndex
          queue.push(toNode, candidate)
        }
      }
    }

    if (!Number.isFinite(distances[endNodeIndex])) {
      throw new RouteError('ROUTE_NOT_FOUND', 'No directed route connects the snapped nodes.', 422, {
        startNodeId: this.runtime.nodeId(startNodeIndex),
        endNodeId: this.runtime.nodeId(endNodeIndex),
      })
    }
    const edgeIndexes = []
    let nodeIndex = endNodeIndex
    while (nodeIndex !== startNodeIndex) {
      const edgeIndex = previousEdges[nodeIndex]
      invariantRoute(edgeIndex >= 0, 'ROUTE_RECONSTRUCTION_FAILED', 'The calculated route could not be reconstructed.', 500)
      edgeIndexes.push(edgeIndex)
      nodeIndex = this.runtime.directedFromNodeIndexes[edgeIndex]
    }
    edgeIndexes.reverse()
    const kpis = summarizeRoute(this.runtime, edgeIndexes, timestamp, profile.solarAvoidanceFactor)
    return {
      profile: { id: profile.id, solarAvoidanceFactor: profile.solarAvoidanceFactor },
      edgeIds: edgeIndexes.map((edgeIndex) => this.runtime.directedEdgeId(edgeIndex)),
      geometry: { type: 'LineString', coordinates: routeGeometry(this.runtime, edgeIndexes, startNodeIndex) },
      kpis,
      diagnostics: { visitedNodeCount, searchMilliseconds: performance.now() - started },
    }
  }
}
