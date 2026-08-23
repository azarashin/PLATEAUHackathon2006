#!/usr/bin/env node

import { createHash } from 'node:crypto'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const EARTH_RADIUS_METERS = 6_371_008.8
const DEFAULT_WALKING_SPEED_METERS_PER_SECOND = 1.4
const EXCLUDED_HIGHWAYS = new Set(['motorway', 'motorway_link', 'trunk', 'trunk_link', 'construction', 'proposed', 'raceway'])

function usage() {
  return `Usage: node tools/road-network/build-pedestrian-graph.mjs \\
  --config <analysis-config.json> --osm <overpass-with-node-ids.json> \\
  --overrides <overrides.geojson> --output <graph.json> --report <quality-report.json> \\
  [--route-start <longitude,latitude> --route-end <longitude,latitude>]`
}

function parseArgs(args) {
  const options = {}
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index]
    if (!argument.startsWith('--')) throw new Error(`Unknown argument: ${argument}`)
    const name = argument.slice(2)
    const value = args[index + 1]
    if (value === undefined || value.startsWith('--')) throw new Error(`Missing value for --${name}`)
    options[name] = value
    index += 1
  }
  for (const name of ['config', 'osm', 'overrides', 'output', 'report']) {
    if (!options[name]) throw new Error(`--${name} is required`)
  }
  if (Boolean(options['route-start']) !== Boolean(options['route-end'])) throw new Error('--route-start and --route-end must be provided together')
  return options
}

function parseCoordinate(value, optionName) {
  const coordinate = value.split(',').map(Number)
  if (coordinate.length !== 2 || !coordinate.every(Number.isFinite)) throw new Error(`${optionName} must be longitude,latitude`)
  return coordinate
}

function validateConfig(config) {
  if (typeof config.areaId !== 'string' || config.areaId.length === 0) throw new Error('Config areaId is required')
  if (!Array.isArray(config.center) || config.center.length !== 2 || !config.center.every(Number.isFinite)) throw new Error('Config center must be [longitude, latitude]')
  if (!Number.isFinite(config.radiusMeters) || config.radiusMeters <= 0) throw new Error('Config radiusMeters must be positive')
  if (!Number.isInteger(config.coordinateZoneId) || config.coordinateZoneId < 1 || config.coordinateZoneId > 19) throw new Error('Config coordinateZoneId must be 1..19')
  const speed = config.walkingSpeedMetersPerSecond ?? DEFAULT_WALKING_SPEED_METERS_PER_SECOND
  if (!Number.isFinite(speed) || speed <= 0) throw new Error('Config walkingSpeedMetersPerSecond must be positive')
  return speed
}

function distanceMeters([longitudeA, latitudeA], [longitudeB, latitudeB]) {
  const radians = Math.PI / 180
  const latitudeARadians = latitudeA * radians
  const latitudeBRadians = latitudeB * radians
  const latitudeDelta = (latitudeB - latitudeA) * radians
  const longitudeDelta = (longitudeB - longitudeA) * radians
  const sinLatitude = Math.sin(latitudeDelta / 2)
  const sinLongitude = Math.sin(longitudeDelta / 2)
  const haversine = sinLatitude ** 2 + Math.cos(latitudeARadians) * Math.cos(latitudeBRadians) * sinLongitude ** 2
  return 2 * EARTH_RADIUS_METERS * Math.asin(Math.min(1, Math.sqrt(haversine)))
}

function localMeters(coordinate, center) {
  const radians = Math.PI / 180
  return [
    (coordinate[0] - center[0]) * radians * EARTH_RADIUS_METERS * Math.cos(center[1] * radians),
    (coordinate[1] - center[1]) * radians * EARTH_RADIUS_METERS,
  ]
}

function segmentIntersectsCircle(fromCoordinate, toCoordinate, center, radiusMeters) {
  const [fromX, fromY] = localMeters(fromCoordinate, center)
  const [toX, toY] = localMeters(toCoordinate, center)
  const deltaX = toX - fromX
  const deltaY = toY - fromY
  const squaredLength = deltaX ** 2 + deltaY ** 2
  const ratio = squaredLength === 0 ? 0 : Math.max(0, Math.min(1, -(fromX * deltaX + fromY * deltaY) / squaredLength))
  const nearestX = fromX + ratio * deltaX
  const nearestY = fromY + ratio * deltaY
  return nearestX ** 2 + nearestY ** 2 <= radiusMeters ** 2
}

function isWalkable(tags, highway) {
  if (typeof highway !== 'string' || EXCLUDED_HIGHWAYS.has(highway)) return false
  if (tags.area === 'yes' || tags.foot === 'no') return false
  if ((tags.access === 'no' || tags.access === 'private') && tags.foot !== 'yes' && tags.foot !== 'designated') return false
  return true
}

function pedestrianArcs(tags, fromNodeId, toNodeId) {
  if (tags['oneway:foot'] === '-1' || tags.foot === 'backward') return [[toNodeId, fromNodeId]]
  if (tags['oneway:foot'] === 'yes' || tags.foot === 'forward') return [[fromNodeId, toNodeId]]
  return [[fromNodeId, toNodeId], [toNodeId, fromNodeId]]
}

function validateOverrides(overrides, areaId) {
  if (overrides?.type !== 'FeatureCollection' || !Array.isArray(overrides.features)) throw new Error('Overrides must be a GeoJSON FeatureCollection')
  const selected = overrides.features.filter((feature) => feature?.properties?.areaId === areaId)
  const ids = new Set()
  for (const feature of selected) {
    const properties = feature.properties
    for (const name of ['id', 'operation', 'reason', 'evidence', 'createdAt', 'reviewer']) {
      if (typeof properties[name] !== 'string' || properties[name].length === 0) throw new Error(`Override ${name} is required`)
    }
    if (ids.has(properties.id)) throw new Error(`Duplicate override id: ${properties.id}`)
    ids.add(properties.id)
    if (properties.operation !== 'remove-edge') throw new Error(`Unsupported override operation: ${properties.operation}`)
    if (typeof properties.sourceEdgeId !== 'string' || properties.sourceEdgeId.length === 0) throw new Error(`Override sourceEdgeId is required: ${properties.id}`)
  }
  return selected
}

function buildGraph(config, osm, overrides) {
  const walkingSpeed = validateConfig(config)
  if (!Array.isArray(osm.elements)) throw new Error('OSM input does not contain elements')
  const selectedOverrides = validateOverrides(overrides, config.areaId)
  const removedSourceEdgeIds = new Set(selectedOverrides.map((feature) => feature.properties.sourceEdgeId))
  const nodes = new Map()
  const physicalEdges = new Map()
  const diagnostics = {
    sourceWayCount: 0,
    walkableWayCount: 0,
    excludedWayCount: 0,
    sourceSegmentCount: 0,
    outsideAreaSegmentCount: 0,
    malformedSegmentCount: 0,
    zeroLengthSegmentCount: 0,
    removedByOverrideCount: 0,
    duplicatePhysicalSegmentCount: 0,
    nodeCoordinateConflictCount: 0,
  }

  const ways = osm.elements.filter((element) => element?.type === 'way').sort((left, right) => left.id - right.id)
  for (const way of ways) {
    diagnostics.sourceWayCount += 1
    const tags = way.tags ?? {}
    if (!isWalkable(tags, tags.highway)) {
      diagnostics.excludedWayCount += 1
      continue
    }
    diagnostics.walkableWayCount += 1
    if (!Array.isArray(way.nodes) || !Array.isArray(way.geometry) || way.nodes.length !== way.geometry.length || way.nodes.length < 2) {
      diagnostics.malformedSegmentCount += 1
      continue
    }
    for (let segmentIndex = 0; segmentIndex < way.nodes.length - 1; segmentIndex += 1) {
      diagnostics.sourceSegmentCount += 1
      const fromGeometry = way.geometry[segmentIndex]
      const toGeometry = way.geometry[segmentIndex + 1]
      const fromCoordinate = [fromGeometry?.lon, fromGeometry?.lat]
      const toCoordinate = [toGeometry?.lon, toGeometry?.lat]
      if (!fromCoordinate.every(Number.isFinite) || !toCoordinate.every(Number.isFinite) || !Number.isInteger(way.nodes[segmentIndex]) || !Number.isInteger(way.nodes[segmentIndex + 1])) {
        diagnostics.malformedSegmentCount += 1
        continue
      }
      if (!segmentIntersectsCircle(fromCoordinate, toCoordinate, config.center, config.radiusMeters)) {
        diagnostics.outsideAreaSegmentCount += 1
        continue
      }
      const sourceEdgeId = `osm-way-${way.id}-${segmentIndex}`
      if (removedSourceEdgeIds.has(sourceEdgeId)) {
        diagnostics.removedByOverrideCount += 1
        continue
      }
      const fromNodeId = `osm-node-${way.nodes[segmentIndex]}`
      const toNodeId = `osm-node-${way.nodes[segmentIndex + 1]}`
      const lengthMeters = distanceMeters(fromCoordinate, toCoordinate)
      if (fromNodeId === toNodeId || lengthMeters <= 0.01) {
        diagnostics.zeroLengthSegmentCount += 1
        continue
      }
      for (const [id, osmNodeId, coordinate] of [[fromNodeId, way.nodes[segmentIndex], fromCoordinate], [toNodeId, way.nodes[segmentIndex + 1], toCoordinate]]) {
        const previous = nodes.get(id)
        if (previous && distanceMeters(previous.coordinate, coordinate) > 0.02) diagnostics.nodeCoordinateConflictCount += 1
        else if (!previous) nodes.set(id, { id, osmNodeId, coordinate })
      }

      const orderedNodeIds = [fromNodeId, toNodeId].sort()
      const pairId = orderedNodeIds.join('|')
      let physicalEdge = physicalEdges.get(pairId)
      if (!physicalEdge) {
        physicalEdge = {
          fromNodeId: orderedNodeIds[0],
          toNodeId: orderedNodeIds[1],
          sourceEdgeIds: [],
          osmWayIds: [],
          highways: [],
          allowedArcs: new Set(),
        }
        physicalEdges.set(pairId, physicalEdge)
      } else {
        diagnostics.duplicatePhysicalSegmentCount += 1
      }
      physicalEdge.sourceEdgeIds.push(sourceEdgeId)
      physicalEdge.osmWayIds.push(way.id)
      physicalEdge.highways.push(tags.highway)
      for (const [arcFrom, arcTo] of pedestrianArcs(tags, fromNodeId, toNodeId)) physicalEdge.allowedArcs.add(`${arcFrom}|${arcTo}`)
    }
  }

  const directedEdges = []
  for (const physicalEdge of [...physicalEdges.values()].sort((left, right) => left.sourceEdgeIds[0].localeCompare(right.sourceEdgeIds[0]))) {
    physicalEdge.sourceEdgeIds.sort()
    physicalEdge.osmWayIds = [...new Set(physicalEdge.osmWayIds)].sort((left, right) => left - right)
    physicalEdge.highways = [...new Set(physicalEdge.highways)].sort()
    const fromNode = nodes.get(physicalEdge.fromNodeId)
    const toNode = nodes.get(physicalEdge.toNodeId)
    const lengthMeters = distanceMeters(fromNode.coordinate, toNode.coordinate)
    const baseId = physicalEdge.sourceEdgeIds[0]
    for (const [arcFrom, arcTo, direction] of [
      [physicalEdge.fromNodeId, physicalEdge.toNodeId, 'forward'],
      [physicalEdge.toNodeId, physicalEdge.fromNodeId, 'backward'],
    ]) {
      if (!physicalEdge.allowedArcs.has(`${arcFrom}|${arcTo}`)) continue
      directedEdges.push({
        id: `${baseId}:${direction}`,
        physicalEdgeId: baseId,
        sourceEdgeIds: physicalEdge.sourceEdgeIds,
        osmWayIds: physicalEdge.osmWayIds,
        highways: physicalEdge.highways,
        fromNodeId: arcFrom,
        toNodeId: arcTo,
        direction,
        coordinates: [nodes.get(arcFrom).coordinate, nodes.get(arcTo).coordinate],
        lengthMeters,
        walkingSeconds: lengthMeters / walkingSpeed,
      })
    }
  }

  return {
    nodes: [...nodes.values()].sort((left, right) => left.osmNodeId - right.osmNodeId),
    edges: directedEdges.sort((left, right) => left.id.localeCompare(right.id)),
    physicalEdgeCount: physicalEdges.size,
    walkingSpeed,
    diagnostics,
    appliedOverrides: selectedOverrides.map((feature) => feature.properties),
  }
}

function analyzeConnectivity(nodes, edges) {
  const neighbors = new Map(nodes.map((node) => [node.id, new Set()]))
  for (const edge of edges) {
    neighbors.get(edge.fromNodeId)?.add(edge.toNodeId)
    neighbors.get(edge.toNodeId)?.add(edge.fromNodeId)
  }
  const visited = new Set()
  const componentSizes = []
  for (const node of nodes) {
    if (visited.has(node.id)) continue
    const queue = [node.id]
    visited.add(node.id)
    let size = 0
    while (queue.length > 0) {
      const current = queue.pop()
      size += 1
      for (const neighbor of neighbors.get(current) ?? []) {
        if (!visited.has(neighbor)) {
          visited.add(neighbor)
          queue.push(neighbor)
        }
      }
    }
    componentSizes.push(size)
  }
  componentSizes.sort((left, right) => right - left)
  return {
    componentCount: componentSizes.length,
    largestComponentNodeCount: componentSizes[0] ?? 0,
    isolatedNodeCount: componentSizes.filter((size) => size === 1).length,
    deadEndNodeCount: [...neighbors.values()].filter((set) => set.size === 1).length,
    smallComponentCount: componentSizes.filter((size) => size <= 10).length,
    largestComponentSizes: componentSizes.slice(0, 20),
  }
}

function projectPointToSegment(coordinate, fromCoordinate, toCoordinate) {
  const [fromX, fromY] = localMeters(fromCoordinate, coordinate)
  const [toX, toY] = localMeters(toCoordinate, coordinate)
  const deltaX = toX - fromX
  const deltaY = toY - fromY
  const squaredLength = deltaX ** 2 + deltaY ** 2
  const ratio = squaredLength === 0 ? 0 : Math.max(0, Math.min(1, -(fromX * deltaX + fromY * deltaY) / squaredLength))
  const snappedCoordinate = [
    fromCoordinate[0] + (toCoordinate[0] - fromCoordinate[0]) * ratio,
    fromCoordinate[1] + (toCoordinate[1] - fromCoordinate[1]) * ratio,
  ]
  return { ratio, coordinate: snappedCoordinate, distanceMeters: distanceMeters(coordinate, snappedCoordinate) }
}

function nearestPhysicalEdge(edges, coordinate) {
  const visited = new Set()
  let nearest
  for (const edge of edges) {
    if (visited.has(edge.physicalEdgeId)) continue
    visited.add(edge.physicalEdgeId)
    const projection = projectPointToSegment(coordinate, edge.coordinates[0], edge.coordinates[1])
    if (!nearest || projection.distanceMeters < nearest.distanceMeters || (projection.distanceMeters === nearest.distanceMeters && edge.physicalEdgeId < nearest.physicalEdgeId)) {
      nearest = { physicalEdgeId: edge.physicalEdgeId, ...projection }
    }
  }
  if (!nearest) throw new Error('Graph has no edges to snap')
  return nearest
}

class MinHeap {
  #values = []
  push(value) {
    this.#values.push(value)
    let index = this.#values.length - 1
    while (index > 0) {
      const parent = Math.floor((index - 1) / 2)
      if (this.#values[parent].cost <= value.cost) break
      this.#values[index] = this.#values[parent]
      index = parent
    }
    this.#values[index] = value
  }
  pop() {
    if (this.#values.length === 0) return undefined
    const minimum = this.#values[0]
    const last = this.#values.pop()
    if (this.#values.length > 0 && last) {
      let index = 0
      while (index * 2 + 1 < this.#values.length) {
        const left = index * 2 + 1
        const right = left + 1
        const child = right < this.#values.length && this.#values[right].cost < this.#values[left].cost ? right : left
        if (this.#values[child].cost >= last.cost) break
        this.#values[index] = this.#values[child]
        index = child
      }
      this.#values[index] = last
    }
    return minimum
  }
  get size() { return this.#values.length }
}

function shortestPath(graph, startCoordinate, endCoordinate) {
  const start = nearestPhysicalEdge(graph.edges, startCoordinate)
  const end = nearestPhysicalEdge(graph.edges, endCoordinate)
  const outgoing = new Map()
  for (const edge of graph.edges) {
    const list = outgoing.get(edge.fromNodeId) ?? []
    list.push(edge)
    outgoing.set(edge.fromNodeId, list)
  }
  const startEdges = graph.edges.filter((edge) => edge.physicalEdgeId === start.physicalEdgeId)
  const endEdges = graph.edges.filter((edge) => edge.physicalEdgeId === end.physicalEdgeId)
  const costs = new Map()
  const previous = new Map()
  const queue = new MinHeap()
  for (const edge of startEdges) {
    const ratio = projectPointToSegment(start.coordinate, edge.coordinates[0], edge.coordinates[1]).ratio
    const cost = (1 - ratio) * edge.walkingSeconds
    if (cost < (costs.get(edge.toNodeId) ?? Infinity)) {
      costs.set(edge.toNodeId, cost)
      previous.set(edge.toNodeId, { edge, partial: 'start', partialCost: cost })
      queue.push({ nodeId: edge.toNodeId, cost })
    }
  }
  const endTargets = endEdges.map((edge) => {
    const ratio = projectPointToSegment(end.coordinate, edge.coordinates[0], edge.coordinates[1]).ratio
    return { nodeId: edge.fromNodeId, edge, extraCost: ratio * edge.walkingSeconds }
  })
  let best
  if (start.physicalEdgeId === end.physicalEdgeId) {
    for (const edge of startEdges) {
      const startRatio = projectPointToSegment(start.coordinate, edge.coordinates[0], edge.coordinates[1]).ratio
      const endRatio = projectPointToSegment(end.coordinate, edge.coordinates[0], edge.coordinates[1]).ratio
      const directCost = (endRatio - startRatio) * edge.walkingSeconds
      if (endRatio >= startRatio && (!best || directCost < best.cost)) best = { cost: directCost, target: undefined, directEdge: edge }
    }
  }
  while (queue.size > 0) {
    const current = queue.pop()
    if (current.cost !== costs.get(current.nodeId)) continue
    if (best && current.cost >= best.cost) break
    for (const target of endTargets) {
      if (target.nodeId !== current.nodeId) continue
      const totalCost = current.cost + target.extraCost
      if (!best || totalCost < best.cost) best = { cost: totalCost, target }
    }
    for (const edge of outgoing.get(current.nodeId) ?? []) {
      const nextCost = current.cost + edge.walkingSeconds
      if (nextCost < (costs.get(edge.toNodeId) ?? Infinity)) {
        costs.set(edge.toNodeId, nextCost)
        previous.set(edge.toNodeId, { edge, partial: undefined })
        queue.push({ nodeId: edge.toNodeId, cost: nextCost })
      }
    }
  }
  if (!best) return { found: false, start, end }
  if (best.directEdge) {
    return { found: true, start, end, edgeIds: [best.directEdge.id], lengthMeters: best.cost * graph.walkingSpeed, walkingSeconds: best.cost }
  }
  const traversed = []
  for (let nodeId = best.target.nodeId; ;) {
    const step = previous.get(nodeId)
    if (!step) throw new Error('Path reconstruction failed')
    traversed.push(step)
    if (step.partial === 'start') break
    nodeId = step.edge.fromNodeId
  }
  traversed.reverse()
  const edgeIds = traversed.filter((step) => step.partial !== 'start' || step.partialCost > 0).map((step) => step.edge.id)
  if (best.target.extraCost > 0) edgeIds.push(best.target.edge.id)
  return {
    found: true,
    start,
    end,
    edgeIds,
    lengthMeters: best.cost * graph.walkingSpeed,
    walkingSeconds: best.cost,
  }
}

function graphFingerprint(graph) {
  const stableCore = {
    nodes: graph.nodes.map((node) => [node.id, node.coordinate]),
    edges: graph.edges.map((edge) => [edge.id, edge.fromNodeId, edge.toNodeId, edge.lengthMeters, edge.walkingSeconds, edge.sourceEdgeIds]),
  }
  return createHash('sha256').update(JSON.stringify(stableCore)).digest('hex')
}

function qualityReport(graph, input) {
  const nodeIds = new Set()
  const edgeIds = new Set()
  const directedConnections = new Set()
  const failures = []
  let duplicateNodeIdCount = 0
  let duplicateEdgeIdCount = 0
  let duplicateDirectedConnectionCount = 0
  let zeroLengthEdgeCount = 0
  let selfLoopCount = 0
  let missingReferenceCount = 0
  for (const node of graph.nodes) {
    if (nodeIds.has(node.id)) duplicateNodeIdCount += 1
    nodeIds.add(node.id)
  }
  for (const edge of graph.edges) {
    if (edgeIds.has(edge.id)) duplicateEdgeIdCount += 1
    edgeIds.add(edge.id)
    const connection = `${edge.fromNodeId}|${edge.toNodeId}`
    if (directedConnections.has(connection)) duplicateDirectedConnectionCount += 1
    directedConnections.add(connection)
    if (!nodeIds.has(edge.fromNodeId) || !nodeIds.has(edge.toNodeId)) missingReferenceCount += 1
    if (edge.fromNodeId === edge.toNodeId) selfLoopCount += 1
    if (!Number.isFinite(edge.lengthMeters) || edge.lengthMeters <= 0.01) zeroLengthEdgeCount += 1
  }
  if (graph.diagnostics.malformedSegmentCount > 0) failures.push('Malformed OSM ways or segments were found')
  if (graph.diagnostics.nodeCoordinateConflictCount > 0) failures.push('A single OSM node ID had conflicting coordinates')
  if (duplicateNodeIdCount > 0) failures.push('Duplicate node IDs were generated')
  if (duplicateEdgeIdCount > 0) failures.push('Duplicate edge IDs were generated')
  if (duplicateDirectedConnectionCount > 0) failures.push('Duplicate directed connections remained after normalization')
  if (missingReferenceCount > 0) failures.push('Edges reference missing nodes')
  if (zeroLengthEdgeCount > 0) failures.push('Zero-length edges were generated')
  if (selfLoopCount > 0) failures.push('Self-loop edges were generated')
  return {
    schemaVersion: 'pedestrian-road-network-quality-report-1.0',
    areaId: input.areaId,
    generatedAt: new Date().toISOString(),
    graphFingerprintSha256: graphFingerprint(graph),
    input,
    counts: {
      nodeCount: graph.nodes.length,
      directedEdgeCount: graph.edges.length,
      physicalEdgeCount: graph.physicalEdgeCount,
      ...graph.diagnostics,
    },
    connectivity: analyzeConnectivity(graph.nodes, graph.edges),
    validation: {
      isValid: failures.length === 0,
      duplicateNodeIdCount,
      duplicateEdgeIdCount,
      duplicateDirectedConnectionCount,
      missingReferenceCount,
      zeroLengthEdgeCount,
      selfLoopCount,
      failures,
    },
    manualOverrides: graph.appliedOverrides,
  }
}

async function main() {
  const options = parseArgs(process.argv.slice(2))
  const [config, osm, overrides] = await Promise.all([
    readFile(resolve(options.config), 'utf8').then(JSON.parse),
    readFile(resolve(options.osm), 'utf8').then(JSON.parse),
    readFile(resolve(options.overrides), 'utf8').then(JSON.parse),
  ])
  const graph = buildGraph(config, osm, overrides)
  const report = qualityReport(graph, {
    areaId: config.areaId,
    configPath: options.config,
    osmPath: options.osm,
    overridesPath: options.overrides,
    osmTimestamp: osm.osm3s?.timestamp_osm_base ?? null,
    walkingSpeedMetersPerSecond: graph.walkingSpeed,
  })
  if (options['route-start']) {
    report.routeVerification = shortestPath(graph, parseCoordinate(options['route-start'], '--route-start'), parseCoordinate(options['route-end'], '--route-end'))
    if (!report.routeVerification.found) throw new Error('Route verification endpoints are disconnected')
  }
  const output = {
    schemaVersion: 'pedestrian-road-network-1.0',
    areaId: config.areaId,
    generatedAt: report.generatedAt,
    graphFingerprintSha256: report.graphFingerprintSha256,
    extent: { center: config.center, radiusMeters: config.radiusMeters },
    coordinateSystems: {
      geographic: { epsg: 4326, axisOrder: ['longitude', 'latitude'] },
      unity: {
        japanPlaneRectangularZoneId: config.coordinateZoneId,
        epsg: 6668 + config.coordinateZoneId,
        coordinateSystem: 'EUN',
        referencePointGeographic: config.center,
        description: 'PLATEAU GeoReference.Project result relative to the projected center; X=east, Y=up, Z=north.',
      },
    },
    walking: {
      defaultSpeedMetersPerSecond: graph.walkingSpeed,
      directionPolicy: 'Bidirectional unless OSM oneway:foot or foot=forward/backward explicitly restricts pedestrian direction.',
    },
    nodes: graph.nodes,
    edges: graph.edges,
  }
  await mkdir(dirname(resolve(options.output)), { recursive: true })
  await mkdir(dirname(resolve(options.report)), { recursive: true })
  await writeFile(resolve(options.output), `${JSON.stringify(output)}\n`)
  await writeFile(resolve(options.report), `${JSON.stringify(report, null, 2)}\n`)
  if (!report.validation.isValid) throw new Error(`Graph validation failed: ${JSON.stringify(report.validation)}`)
  console.log(`ROAD_GRAPH_BUILT area=${output.areaId} nodes=${graph.nodes.length} directedEdges=${graph.edges.length} components=${report.connectivity.componentCount} fingerprint=${report.graphFingerprintSha256}`)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message)
    console.error(usage())
    process.exitCode = 1
  })
}

export { buildGraph, graphFingerprint, qualityReport, shortestPath }
