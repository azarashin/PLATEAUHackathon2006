#!/usr/bin/env node

import { createHash } from 'node:crypto'
import { mkdir, readFile, rename, unlink, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { validateHourlyOutput } from '../hourly-environment-cost/validate-hourly-output.mjs'
import {
  aggregateTimeSlices,
  coordinateRoundTrip,
  equalCoordinate,
  validateGraph,
} from './build-environment-cost-road-network.mjs'

const STATUS_TO_CODE = Object.freeze({ missing: 0, partial: 1, available: 2 })
const CODE_TO_STATUS = Object.freeze(['missing', 'partial', 'available'])
const FORMULA_TOLERANCE_SECONDS = 1e-6
const PEDESTRIAN_NETWORK_SAFETY_CONTRACT_VERSION = 'pedestrian-network-safety-1.0'

function usage() {
  return `Usage: node --max-old-space-size=8192 tools/environment-cost-network/build-environment-cost-server-bundle.mjs \\
  --graph <pedestrian-road-network.json> --environment <hourly-environment-cost.json> \\
  --bundle-directory <server-bundle-directory> --report <integration-report.json> \\
  [--allow-unmatched-as-missing] [--provenance analysis|fixture]`
}

function parseArgs(args) {
  const options = { provenance: 'analysis' }
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index]
    if (argument === '--allow-unmatched-as-missing') {
      options.allowUnmatchedAsMissing = true
      continue
    }
    if (!argument.startsWith('--')) throw new Error(`Unknown argument: ${argument}`)
    const name = argument.slice(2)
    const value = args[index + 1]
    if (value === undefined || value.startsWith('--')) throw new Error(`Missing value for --${name}`)
    options[name] = value
    index += 1
  }
  for (const name of ['graph', 'environment', 'bundle-directory', 'report']) {
    if (!options[name]) throw new Error(`--${name} is required`)
  }
  if (!['analysis', 'fixture'].includes(options.provenance)) throw new Error('--provenance must be analysis or fixture')
  return options
}

function invariant(condition, message) {
  if (!condition) throw new Error(message)
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex')
}

function contentFingerprint(document) {
  const stable = { ...document }
  delete stable.contentFingerprintSha256
  return sha256(JSON.stringify(stable))
}

function validateInputCompatibility(graph, environment) {
  validateGraph(graph)
  const environmentSummary = validateHourlyOutput(environment)
  invariant(graph.areaId === environment.areaId, 'graph and environment areaId do not match')
  invariant(equalCoordinate(graph.extent.center, environment.center), 'graph and environment centers do not match')
  invariant(Math.abs(graph.extent.radiusMeters - environment.radiusMeters) <= 1e-9, 'graph and environment radii do not match')
  invariant(graph.coordinateSystems.unity.japanPlaneRectangularZoneId === environment.coordinateZoneId, 'graph and environment coordinate zones do not match')
  invariant(Math.abs(graph.walking.defaultSpeedMetersPerSecond - environment.settings.walkingSpeedMetersPerSecond) <= 1e-12, 'graph and environment walking speeds do not match')
  return environmentSummary
}

function policyScenarioMetadata(environment) {
  const scenario = environment.scenario
  if (!scenario || typeof scenario.id !== 'string' || scenario.id === 'baseline') {
    return { id: 'baseline', label: '現状', fingerprintSha256: environment.resultFingerprintSha256 }
  }
  invariant(/^[a-z][a-z0-9-]{0,31}$/.test(scenario.id), 'environment scenario id is invalid')
  invariant(typeof scenario.fingerprintSha256 === 'string' && /^[0-9a-f]{64}$/.test(scenario.fingerprintSha256), 'environment scenario fingerprint is invalid')
  return { id: scenario.id, label: `施策: ${scenario.id}`, fingerprintSha256: scenario.fingerprintSha256 }
}

function normalizedPhysicalEdges(graph) {
  const physical = new Map()
  for (const edge of [...graph.edges].sort((left, right) => left.id.localeCompare(right.id))) {
    const previous = physical.get(edge.physicalEdgeId)
    if (!previous) {
      physical.set(edge.physicalEdgeId, {
        id: edge.physicalEdgeId,
        sourceEdgeIds: [...edge.sourceEdgeIds].sort(),
        walkingSeconds: edge.walkingSeconds,
      })
      continue
    }
    invariant(JSON.stringify(previous.sourceEdgeIds) === JSON.stringify([...edge.sourceEdgeIds].sort()), `physical edge source IDs differ by direction: ${edge.physicalEdgeId}`)
    invariant(Math.abs(previous.walkingSeconds - edge.walkingSeconds) <= 1e-9, `physical edge walking time differs by direction: ${edge.physicalEdgeId}`)
  }
  return [...physical.values()].sort((left, right) => left.id.localeCompare(right.id))
}

function buildTopology(graph, physicalEdges) {
  const sortedNodes = [...graph.nodes].sort((left, right) => left.id.localeCompare(right.id))
  const nodeIndex = new Map(sortedNodes.map((node, index) => [node.id, index]))
  const physicalIndex = new Map(physicalEdges.map((edge, index) => [edge.id, index]))
  const topology = {
    schemaVersion: 'environment-cost-server-topology-1.0',
    areaId: graph.areaId,
    graphFingerprintSha256: graph.graphFingerprintSha256,
    coordinateReferenceSystem: {
      geometryEpsg: 4326,
      axisOrder: ['longitude', 'latitude'],
      projectedEpsg: graph.coordinateSystems.unity.epsg,
      coordinateZoneId: graph.coordinateSystems.unity.japanPlaneRectangularZoneId,
      unityAxisConvention: 'EUN',
      referencePointGeographic: graph.extent.center,
    },
    counts: {
      nodeCount: sortedNodes.length,
      physicalEdgeCount: physicalEdges.length,
      directedEdgeCount: graph.edges.length,
    },
    nodes: sortedNodes.map((node) => {
      invariant(node.id === `osm-node-${node.osmNodeId}`, `server topology cannot derive node ID: ${node.id}`)
      return [node.osmNodeId, node.coordinate[0], node.coordinate[1]]
    }),
    physicalEdges: physicalEdges.map((edge) => [edge.id, edge.sourceEdgeIds]),
    directedEdges: [...graph.edges].sort((left, right) => left.id.localeCompare(right.id)).map((edge) => [
      physicalIndex.get(edge.physicalEdgeId),
      nodeIndex.get(edge.fromNodeId),
      nodeIndex.get(edge.toNodeId),
      edge.direction === 'forward' ? 0 : 1,
      edge.lengthMeters,
      edge.walkingSeconds,
    ]),
  }
  topology.contentFingerprintSha256 = contentFingerprint(topology)
  return topology
}

function buildCostSlices(graph, environment, topology, physicalEdges, allowUnmatchedAsMissing) {
  const sourceCosts = new Map(environment.edges.map((edge) => [edge.id, edge]))
  const graphSourceIds = new Set(physicalEdges.flatMap((edge) => edge.sourceEdgeIds))
  const canonicalGraphEdges = new Map()
  for (const edge of graph.edges) if (!canonicalGraphEdges.has(edge.physicalEdgeId)) canonicalGraphEdges.set(edge.physicalEdgeId, edge)
  const timestamps = environment.edges[0].hourly.map((slice) => slice.timestamp)
  const aggregatedByPhysical = []
  const unmatchedPhysicalEdgeIds = []
  const partiallyMatchedPhysicalEdgeIds = []
  let matchedPhysicalEdgeCount = 0

  for (const physical of physicalEdges) {
    const matches = physical.sourceEdgeIds.map((id) => sourceCosts.get(id)).filter(Boolean)
    if (matches.length > 0 && matches.length !== physical.sourceEdgeIds.length) partiallyMatchedPhysicalEdgeIds.push(physical.id)
    if (matches.length === 0) unmatchedPhysicalEdgeIds.push(physical.id)
    else matchedPhysicalEdgeCount += 1
    aggregatedByPhysical.push(aggregateTimeSlices(canonicalGraphEdges.get(physical.id), matches, timestamps))
  }
  invariant(partiallyMatchedPhysicalEdgeIds.length === 0, `physical edges are only partially represented by environment costs: ${partiallyMatchedPhysicalEdgeIds.slice(0, 10).join(', ')}`)
  if (unmatchedPhysicalEdgeIds.length > 0 && !allowUnmatchedAsMissing) {
    throw new Error(`${unmatchedPhysicalEdgeIds.length} physical graph edges have no environment-cost source; pass --allow-unmatched-as-missing to preserve them as explicit missing values`)
  }

  const costSlices = timestamps.map((timestamp, timestampIndex) => {
    const statusCounts = { available: 0, partial: 0, missing: 0 }
    const costs = aggregatedByPhysical.map((timeSlices, physicalIndex) => {
      const slice = timeSlices[timestampIndex]
      statusCounts[slice.status] += 1
      const coverage = slice.sampleCoverage
      return [
        STATUS_TO_CODE[slice.status],
        coverage.sampleCount,
        coverage.validSampleCount,
        coverage.noGroundSampleCount,
        slice.values.shadeRatio,
        slice.values.solarExposureSeconds,
      ]
    })
    const document = {
      schemaVersion: 'environment-cost-server-cost-slice-1.0',
      areaId: graph.areaId,
      timestamp,
      topologyContentFingerprintSha256: topology.contentFingerprintSha256,
      environmentCostFingerprintSha256: environment.resultFingerprintSha256,
      physicalEdgeCount: physicalEdges.length,
      statusCounts,
      costs,
    }
    document.contentFingerprintSha256 = contentFingerprint(document)
    return document
  })
  return {
    costSlices,
    diagnostics: {
      matchedPhysicalEdgeCount,
      unmatchedPhysicalEdgeCount: unmatchedPhysicalEdgeIds.length,
      unmatchedPhysicalEdgeIds,
      partiallyMatchedPhysicalEdgeCount: partiallyMatchedPhysicalEdgeIds.length,
      ignoredEnvironmentSourceEdgeCount: environment.edges.filter((edge) => !graphSourceIds.has(edge.id)).length,
    },
  }
}

function validateTopology(topology) {
  const isV2 = topology.schemaVersion === 'environment-cost-server-topology-2.0'
  invariant(isV2 || topology.schemaVersion === 'environment-cost-server-topology-1.0', 'server topology schemaVersion is invalid')
  invariant(topology.nodes.length === topology.counts.nodeCount, 'server topology node count mismatch')
  invariant(topology.physicalEdges.length === topology.counts.physicalEdgeCount, 'server topology physical edge count mismatch')
  invariant(topology.directedEdges.length === topology.counts.directedEdgeCount, 'server topology directed edge count mismatch')
  invariant(topology.contentFingerprintSha256 === contentFingerprint(topology), 'server topology content fingerprint mismatch')
  const nodeSourceIds = new Set()
  for (const [sourceNodeId, longitude, latitude] of topology.nodes) {
    invariant((isV2 ? typeof sourceNodeId === 'string' && sourceNodeId.length > 0 : Number.isSafeInteger(sourceNodeId)) && !nodeSourceIds.has(sourceNodeId), `invalid or duplicate server node: ${sourceNodeId}`)
    nodeSourceIds.add(sourceNodeId)
    invariant(Number.isFinite(longitude) && longitude >= -180 && longitude <= 180 && Number.isFinite(latitude) && latitude >= -90 && latitude <= 90, `invalid server node coordinate: ${sourceNodeId}`)
  }
  const physicalIds = new Set()
  const physicalDirections = isV2 ? new Array(topology.physicalEdges.length).fill(0) : null
  for (let physicalIndex = 0; physicalIndex < topology.physicalEdges.length; physicalIndex += 1) {
    const physicalEdge = topology.physicalEdges[physicalIndex]
    const [physicalEdgeId, sourceOrGeometry] = physicalEdge
    invariant(typeof physicalEdgeId === 'string' && !physicalIds.has(physicalEdgeId), `invalid or duplicate physical edge: ${physicalEdgeId}`)
    physicalIds.add(physicalEdgeId)
    if (isV2) {
      const [, fromNodeIndex, toNodeIndex, geometry] = physicalEdge
      invariant(Number.isInteger(fromNodeIndex) && fromNodeIndex >= 0 && fromNodeIndex < topology.nodes.length && Number.isInteger(toNodeIndex) && toNodeIndex >= 0 && toNodeIndex < topology.nodes.length && fromNodeIndex !== toNodeIndex, `invalid physical edge nodes: ${physicalEdgeId}`)
      invariant(Array.isArray(geometry) && geometry.length >= 2 && geometry.every((point) => Array.isArray(point) && point.length === 2 && Number.isFinite(point[0]) && Number.isFinite(point[1])), `invalid physical geometry: ${physicalEdgeId}`)
      invariant(coordinatesMatch(geometry[0], topology.nodes[fromNodeIndex].slice(1)) && coordinatesMatch(geometry[geometry.length - 1], topology.nodes[toNodeIndex].slice(1)), `physical geometry endpoint does not match node: ${physicalEdgeId}`)
    } else {
      invariant(Array.isArray(sourceOrGeometry) && sourceOrGeometry.length > 0 && new Set(sourceOrGeometry).size === sourceOrGeometry.length, `invalid source edges: ${physicalEdgeId}`)
    }
  }
  for (let index = 0; index < topology.directedEdges.length; index += 1) {
    const [physicalIndex, fromNodeIndex, toNodeIndex, directionCode, lengthMeters, walkingSeconds] = topology.directedEdges[index]
    invariant(Number.isInteger(physicalIndex) && physicalIndex >= 0 && physicalIndex < topology.physicalEdges.length, `invalid directed physical index: ${index}`)
    invariant(Number.isInteger(fromNodeIndex) && fromNodeIndex >= 0 && fromNodeIndex < topology.nodes.length, `invalid directed from-node index: ${index}`)
    invariant(Number.isInteger(toNodeIndex) && toNodeIndex >= 0 && toNodeIndex < topology.nodes.length, `invalid directed to-node index: ${index}`)
    invariant(fromNodeIndex !== toNodeIndex, `self-loop directed edge: ${index}`)
    invariant(directionCode === 0 || directionCode === 1, `invalid directed edge direction code: ${index}`)
    invariant(Number.isFinite(lengthMeters) && lengthMeters > 0 && Number.isFinite(walkingSeconds) && walkingSeconds > 0, `invalid directed edge measurement: ${index}`)
    if (isV2) {
      const [, physicalFromNodeIndex, physicalToNodeIndex] = topology.physicalEdges[physicalIndex]
      const isForward = fromNodeIndex === physicalFromNodeIndex && toNodeIndex === physicalToNodeIndex
      const isBackward = fromNodeIndex === physicalToNodeIndex && toNodeIndex === physicalFromNodeIndex
      invariant(isForward || isBackward, `directed edge does not follow physical endpoints: ${index}`)
      invariant(directionCode === (isForward ? 0 : 1), `directed edge direction code disagrees with endpoints: ${index}`)
      physicalDirections[physicalIndex] += 1
      invariant(physicalDirections[physicalIndex] <= 2, `physical edge has duplicate directed direction: ${physicalIndex}`)
    }
  }
  if (isV2) invariant(physicalDirections.every((count) => count > 0), 'a v2 physical edge has no directed edge')
}

function physicalWalkingSeconds(topology) {
  const values = new Array(topology.physicalEdges.length)
  for (const [physicalIndex, , , , , walkingSeconds] of topology.directedEdges) {
    if (values[physicalIndex] === undefined) values[physicalIndex] = walkingSeconds
    else invariant(Math.abs(values[physicalIndex] - walkingSeconds) <= 1e-9, `walking time differs by direction: ${physicalIndex}`)
  }
  invariant(values.every((value) => Number.isFinite(value) && value > 0), 'a physical edge has no directed walking time')
  return values
}

function validateCostSlice(slice, topology, walkingByPhysical) {
  const expectedSchema = topology.schemaVersion === 'environment-cost-server-topology-2.0' ? 'environment-cost-server-cost-slice-2.0' : 'environment-cost-server-cost-slice-1.0'
  invariant(slice.schemaVersion === expectedSchema, 'server cost slice schemaVersion is invalid')
  invariant(slice.areaId === topology.areaId, `server cost area mismatch: ${slice.timestamp}`)
  invariant(slice.topologyContentFingerprintSha256 === topology.contentFingerprintSha256, `server cost topology fingerprint mismatch: ${slice.timestamp}`)
  invariant(slice.physicalEdgeCount === topology.physicalEdges.length && slice.costs.length === topology.physicalEdges.length, `server cost physical edge count mismatch: ${slice.timestamp}`)
  invariant(slice.contentFingerprintSha256 === contentFingerprint(slice), `server cost content fingerprint mismatch: ${slice.timestamp}`)
  const statusCounts = { available: 0, partial: 0, missing: 0 }
  for (let index = 0; index < slice.costs.length; index += 1) {
    const [statusCode, sampleCount, validSampleCount, noGroundSampleCount, shadeRatio, solarExposureSeconds] = slice.costs[index]
    const status = CODE_TO_STATUS[statusCode]
    invariant(status !== undefined, `invalid cost status code: ${slice.timestamp} ${index}`)
    statusCounts[status] += 1
    invariant([sampleCount, validSampleCount, noGroundSampleCount].every(Number.isInteger), `non-integer sample coverage: ${slice.timestamp} ${index}`)
    invariant(sampleCount >= 0 && validSampleCount >= 0 && noGroundSampleCount >= 0 && validSampleCount + noGroundSampleCount === sampleCount, `invalid sample coverage: ${slice.timestamp} ${index}`)
    if (status === 'missing') {
      invariant(validSampleCount === 0 && shadeRatio === null && solarExposureSeconds === null, `invalid missing cost: ${slice.timestamp} ${index}`)
      continue
    }
    invariant(Number.isFinite(shadeRatio) && shadeRatio >= 0 && shadeRatio <= 1 && Number.isFinite(solarExposureSeconds), `invalid calculated cost: ${slice.timestamp} ${index}`)
    invariant(status === (noGroundSampleCount === 0 ? 'available' : 'partial'), `cost status and coverage disagree: ${slice.timestamp} ${index}`)
    const expectedExposure = walkingByPhysical[index] * (1 - shadeRatio)
    invariant(Math.abs(expectedExposure - solarExposureSeconds) <= FORMULA_TOLERANCE_SECONDS, `solar exposure formula mismatch: ${slice.timestamp} ${index}`)
  }
  invariant(JSON.stringify(statusCounts) === JSON.stringify(slice.statusCounts), `server cost status counts mismatch: ${slice.timestamp}`)
}

export function buildServerBundleDocuments(graph, environment, options = {}) {
  if (graph?.schemaVersion === 'environment-cost-pedestrian-network-2.0') {
    return buildV2ServerBundleDocuments(graph, environment, options)
  }
  const environmentSummary = validateInputCompatibility(graph, environment)
  const physicalEdges = normalizedPhysicalEdges(graph)
  const topology = buildTopology(graph, physicalEdges)
  validateTopology(topology)
  const { costSlices, diagnostics: joinDiagnostics } = buildCostSlices(
    graph,
    environment,
    topology,
    physicalEdges,
    options.allowUnmatchedAsMissing === true,
  )
  const walkingByPhysical = physicalWalkingSeconds(topology)
  for (const slice of costSlices) validateCostSlice(slice, topology, walkingByPhysical)
  const timestamps = costSlices.map((slice) => slice.timestamp)
  return {
    topology,
    costSlices,
    manifestMetadata: {
      dataset: {
        id: `${graph.areaId}-environment-cost-server-bundle-v1`,
        provenance: options.provenance ?? 'analysis',
        generatedAt: environment.generatedAt,
      },
      inputs: {
        roadGraphFingerprintSha256: graph.graphFingerprintSha256,
        environmentCostAnalysisKey: environment.analysisKey,
        environmentCostFingerprintSha256: environment.resultFingerprintSha256,
      },
      area: {
        areaId: graph.areaId,
        center: graph.extent.center,
        radiusMeters: graph.extent.radiusMeters,
      },
      scenario: {
        referenceDate: environment.settings.date,
        timezone: environment.settings.timezone,
        availableTimestamps: timestamps,
        defaultTimestamp: timestamps[Math.floor((timestamps.length - 1) / 2)],
      },
      policyScenario: policyScenarioMetadata(environment),
      costFormula: {
        shadeRatioUnit: 'ratio',
        solarExposureSecondsUnit: 's',
        solarExposureSeconds: 'walkingSeconds * (1 - shadeRatio)',
        missingValuePolicy: 'preserve-null',
      },
      encoding: {
        node: ['sourceNodeId', 'longitude', 'latitude'],
        physicalEdge: ['physicalEdgeId', 'sourceEdgeIds'],
        directedEdge: ['physicalEdgeIndex', 'fromNodeIndex', 'toNodeIndex', 'directionCode', 'lengthMeters', 'walkingSeconds'],
        directionCodes: { forward: 0, backward: 1 },
        cost: ['statusCode', 'sampleCount', 'validSampleCount', 'noGroundSampleCount', 'shadeRatio', 'solarExposureSeconds'],
        statusCodes: STATUS_TO_CODE,
      },
    },
    diagnostics: {
      sourceEnvironmentEdgeCount: environmentSummary.edgeCount,
      coordinateVerification: coordinateRoundTrip(graph),
      ...joinDiagnostics,
    },
  }
}

function validateV2Inputs(graph, result) {
  invariant(result?.schemaVersion === 'environment-cost-runtime-shade-result-0.1' && result.status === 'completed', 'v2 environment result must be a completed runtime shade result')
  invariant(typeof graph.areaId === 'string' && graph.areaId === result.areaId, 'v2 graph and environment areaId do not match')
  invariant(typeof graph.graphFingerprintSha256 === 'string' && /^[0-9a-f]{64}$/.test(graph.graphFingerprintSha256), 'v2 graph fingerprint is invalid')
  invariant(Array.isArray(graph.nodes) && graph.nodes.length >= 2 && Array.isArray(graph.physicalEdges) && graph.physicalEdges.length > 0 && Array.isArray(graph.edges) && graph.edges.length > 0, 'v2 sidewalk graph is incomplete')
  const provenance = result.provenance
  invariant(provenance && provenance.graphFingerprintSha256 === graph.graphFingerprintSha256, 'v2 graph and environment fingerprints do not match')
  invariant(Array.isArray(provenance.center) && provenance.center.length === 2 && Array.isArray(graph.extent?.center) && equalCoordinate(provenance.center, graph.extent.center), 'v2 graph and environment centers do not match')
  invariant(Math.abs(provenance.radiusMeters - graph.extent.radiusMeters) <= 1e-9, 'v2 graph and environment radii do not match')
  invariant(Array.isArray(provenance.hours) && provenance.hours.length > 0 && new Set(provenance.hours).size === provenance.hours.length, 'v2 environment hours are invalid')
  invariant(Array.isArray(result.edges) && result.edges.length > 0, 'v2 environment edges are missing')
  const physicalIds = new Set(graph.physicalEdges.map((edge) => edge.id))
  invariant(physicalIds.size === graph.physicalEdges.length && [...physicalIds].every((id) => typeof id === 'string' && id.length > 0), 'v2 physical edge IDs are invalid')
  const graphNodes = new Map(graph.nodes.map((node) => [node.id, node]))
  invariant(graphNodes.size === graph.nodes.length && [...graphNodes.keys()].every((id) => typeof id === 'string' && id.length > 0), 'v2 graph node IDs are invalid')
  const directionKeys = new Set()
  const directedByPhysical = new Map()
  for (const physical of graph.physicalEdges) {
    invariant(graphNodes.has(physical.fromNodeId) && graphNodes.has(physical.toNodeId) && physical.fromNodeId !== physical.toNodeId, `v2 physical edge nodes are invalid: ${physical.id}`)
    invariant(Array.isArray(physical.geometry) && physical.geometry.length >= 2, `v2 physical edge geometry is invalid: ${physical.id}`)
    invariant(coordinatesMatch(physical.geometry[0], graphNodes.get(physical.fromNodeId).coordinate) && coordinatesMatch(physical.geometry[physical.geometry.length - 1], graphNodes.get(physical.toNodeId).coordinate), `v2 physical edge geometry does not match nodes: ${physical.id}`)
    directedByPhysical.set(physical.id, 0)
  }
  invariant(result.edges.length === physicalIds.size, 'v2 environment must contain exactly one result per physical edge')
  const resultIds = new Set()
  for (const edge of result.edges) {
    invariant(typeof edge?.id === 'string' && edge.id.length > 0 && resultIds.add(edge.id), `v2 environment edge ID is invalid or duplicated: ${edge?.id ?? '<missing>'}`)
    invariant(physicalIds.has(edge.id), `v2 environment edge is not a graph physical edge: ${edge.id}`)
    invariant(Array.isArray(edge.hourly) && edge.hourly.length === provenance.hours.length, `v2 environment hourly data is incomplete: ${edge.id}`)
  }
  invariant(resultIds.size === physicalIds.size && [...physicalIds].every((id) => resultIds.has(id)), 'v2 environment edge IDs do not exactly match graph physical edge IDs')
  for (const edge of graph.edges) {
    const physical = graph.physicalEdges.find((item) => item.id === edge.physicalEdgeId)
    invariant(physical, `v2 directed edge physical ID is invalid: ${edge?.physicalEdgeId ?? '<missing>'}`)
    const forward = edge.fromNodeId === physical.fromNodeId && edge.toNodeId === physical.toNodeId
    const backward = edge.fromNodeId === physical.toNodeId && edge.toNodeId === physical.fromNodeId
    invariant(forward || backward, `v2 directed edge does not follow physical edge direction: ${edge?.id ?? '<missing>'}`)
    const directionCode = forward ? 0 : 1
    const directionKey = `${physical.id}\u0000${directionCode}`
    invariant(!directionKeys.has(directionKey), `v2 directed edge duplicates a physical direction: ${physical.id}`)
    directionKeys.add(directionKey)
    directedByPhysical.set(physical.id, directedByPhysical.get(physical.id) + 1)
  }
  invariant([...directedByPhysical.values()].every((count) => count > 0), 'v2 physical edge has no directed edge')
  invariant(typeof provenance.resultFingerprintSha256 === 'string' && /^[0-9a-f]{64}$/.test(provenance.resultFingerprintSha256), 'v2 environment result fingerprint is invalid')
  return provenance
}

function coordinatesMatch(left, right) {
  return Array.isArray(left) && Array.isArray(right) && left.length === 2 && right.length === 2
    && Number.isFinite(left[0]) && Number.isFinite(left[1]) && Number.isFinite(right[0]) && Number.isFinite(right[1])
    && Math.abs(left[0] - right[0]) <= 1e-9 && Math.abs(left[1] - right[1]) <= 1e-9
}

function v2Quality(graph, provenance) {
  const quality = provenance.networkQuality
  invariant(quality && typeof quality === 'object', 'v2 environment network quality is missing')
  invariant(quality.qualityContractVersion === PEDESTRIAN_NETWORK_SAFETY_CONTRACT_VERSION, 'v2 environment network quality contract is missing or legacy-unverified')
  invariant(typeof quality.sourceSchemaVersion === 'string' && quality.sourceSchemaVersion === '0.2', 'v2 network quality source contract is invalid')
  invariant(quality.status === 'accepted', 'v2 network quality is not accepted')
  // Shared Japanese streets may legitimately use representative centerlines. Keep
  // these ratios as diagnostics, not as a false safety gate.
  invariant(Number.isFinite(quality.explicitOrDerivedRatio) && quality.explicitOrDerivedRatio >= 0 && quality.explicitOrDerivedRatio <= 1 && Number.isFinite(quality.fallbackRatio) && quality.fallbackRatio >= 0 && quality.fallbackRatio <= 1 && Math.abs(quality.explicitOrDerivedRatio + quality.fallbackRatio - 1) <= 1e-6, 'v2 network quality ratios are invalid')
  invariant(Array.isArray(quality.validationFailures) && quality.validationFailures.length === 0 && Array.isArray(quality.validationWarnings), 'v2 network quality safety audit is incomplete or has validation failures')
  return {
    qualityContractVersion: quality.qualityContractVersion,
    status: typeof quality.status === 'string' ? quality.status : 'unverified',
    explicitOrDerivedRatio: quality.explicitOrDerivedRatio,
    fallbackRatio: quality.fallbackRatio,
    sourceSchemaVersion: quality.sourceSchemaVersion,
    validationFailures: [...quality.validationFailures],
    validationWarnings: [...quality.validationWarnings],
  }
}

function buildV2Topology(graph, quality) {
  const nodes = [...graph.nodes].sort((left, right) => left.id.localeCompare(right.id))
  const physicalEdges = [...graph.physicalEdges].sort((left, right) => left.id.localeCompare(right.id))
  const nodeIndex = new Map(nodes.map((node, index) => [node.id, index]))
  const physicalIndex = new Map(physicalEdges.map((edge, index) => [edge.id, index]))
  const physicalById = new Map(physicalEdges.map((edge) => [edge.id, edge]))
  const topology = {
    schemaVersion: 'environment-cost-server-topology-2.0', areaId: graph.areaId, graphFingerprintSha256: graph.graphFingerprintSha256,
    coordinateReferenceSystem: { geometryEpsg: 4326, axisOrder: ['longitude', 'latitude'], ...(graph.coordinateReferenceSystem ?? {}) },
    networkQuality: quality,
    counts: { nodeCount: nodes.length, physicalEdgeCount: physicalEdges.length, directedEdgeCount: graph.edges.length },
    nodes: nodes.map((node) => [node.id, node.coordinate[0], node.coordinate[1]]),
    physicalEdges: physicalEdges.map((edge) => [edge.id, nodeIndex.get(edge.fromNodeId), nodeIndex.get(edge.toNodeId), edge.geometry, edge.source ?? null, edge.facility ?? null, edge.side ?? null, edge.level ?? null, edge.fallback === true]),
    directedEdges: [...graph.edges].sort((left, right) => left.id.localeCompare(right.id)).map((edge) => [
      physicalIndex.get(edge.physicalEdgeId), nodeIndex.get(edge.fromNodeId), nodeIndex.get(edge.toNodeId),
      edge.fromNodeId === physicalById.get(edge.physicalEdgeId).fromNodeId ? 0 : 1, physicalById.get(edge.physicalEdgeId).lengthMeters, edge.walkingSeconds,
    ]),
  }
  topology.contentFingerprintSha256 = contentFingerprint(topology)
  validateTopology(topology)
  return topology
}

function v2CostSlices(graph, result, topology) {
  const provenance = result.provenance
  const byPhysicalId = new Map(result.edges.map((edge) => [edge.id, edge]))
  const timestamps = byPhysicalId.get(topology.physicalEdges[0][0]).hourly.map((slice) => slice.timestamp)
  const costSlices = timestamps.map((timestamp, hourIndex) => {
    const statusCounts = { available: 0, partial: 0, missing: 0 }
    const costs = topology.physicalEdges.map(([physicalId]) => {
      const hourly = byPhysicalId.get(physicalId).hourly[hourIndex]
      invariant(hourly.timestamp === timestamp, `v2 environment timestamps differ: ${physicalId}`)
      invariant(['available', 'partial', 'missing'].includes(hourly.status), `v2 environment status is invalid: ${physicalId}`)
      statusCounts[hourly.status] += 1
      return [STATUS_TO_CODE[hourly.status], hourly.sampleCount, hourly.validSampleCount, hourly.noGroundSampleCount,
        hourly.status === 'missing' ? null : hourly.shadeRatio, hourly.status === 'missing' ? null : hourly.solarExposureSeconds]
    })
    const document = { schemaVersion: 'environment-cost-server-cost-slice-2.0', areaId: graph.areaId, timestamp,
      topologyContentFingerprintSha256: topology.contentFingerprintSha256, environmentCostFingerprintSha256: provenance.resultFingerprintSha256,
      physicalEdgeCount: topology.physicalEdges.length, statusCounts, costs }
    document.contentFingerprintSha256 = contentFingerprint(document)
    return document
  })
  return costSlices
}

function buildV2ServerBundleDocuments(graph, result, options) {
  const provenance = validateV2Inputs(graph, result)
  const quality = v2Quality(graph, provenance)
  const topology = buildV2Topology(graph, quality)
  const costSlices = v2CostSlices(graph, result, topology)
  const walkingByPhysical = physicalWalkingSeconds(topology)
  for (const slice of costSlices) validateCostSlice(slice, topology, walkingByPhysical)
  const timestamps = costSlices.map((slice) => slice.timestamp)
  const scenarioId = provenance.scenarioId || 'baseline'
  invariant(/^[a-z][a-z0-9-]{0,31}$/.test(scenarioId), 'v2 scenario id is invalid')
  return {
    schemaVersion: 'environment-cost-server-bundle-2.0', topology, costSlices,
    manifestMetadata: {
      dataset: { id: `${graph.areaId}-environment-cost-server-bundle-v2`, provenance: options.provenance ?? 'analysis', generatedAt: result.generatedAtUtc },
      inputs: { roadGraphFingerprintSha256: graph.graphFingerprintSha256, environmentCostFingerprintSha256: provenance.resultFingerprintSha256 },
      area: { areaId: graph.areaId, center: graph.extent.center, radiusMeters: graph.extent.radiusMeters },
      scenario: { referenceDate: provenance.analysisDate, timezone: provenance.timezone, availableTimestamps: timestamps, defaultTimestamp: timestamps[Math.floor((timestamps.length - 1) / 2)] },
      policyScenario: { id: scenarioId, label: scenarioId === 'baseline' ? '現状' : `施策 ${scenarioId}`, fingerprintSha256: provenance.policyFingerprintSha256 || provenance.resultFingerprintSha256 },
      networkQuality: quality,
      costFormula: { shadeRatioUnit: 'ratio', solarExposureSecondsUnit: 's', solarExposureSeconds: 'walkingSeconds * (1 - shadeRatio)', missingValuePolicy: 'preserve-null' },
      encoding: { node: ['nodeId', 'longitude', 'latitude'], physicalEdge: ['physicalEdgeId', 'geometry', 'source', 'facility', 'side', 'level', 'fallback'], directedEdge: ['physicalEdgeIndex', 'fromNodeIndex', 'toNodeIndex', 'directionCode', 'lengthMeters', 'walkingSeconds'], directionCodes: { forward: 0, backward: 1 }, cost: ['statusCode', 'sampleCount', 'validSampleCount', 'noGroundSampleCount', 'shadeRatio', 'solarExposureSeconds'], statusCodes: STATUS_TO_CODE },
    },
    diagnostics: { sourceEnvironmentEdgeCount: result.edges.length, v2PhysicalEdgeCount: graph.physicalEdges.length },
  }
}

function serializedDocument(document) {
  const text = `${JSON.stringify(document)}\n`
  return { text, bytes: Buffer.byteLength(text), sha256: sha256(text) }
}

async function writeTextAtomic(path, text) {
  const absolute = resolve(path)
  await mkdir(dirname(absolute), { recursive: true })
  const temporary = `${absolute}.partial`
  try {
    await writeFile(temporary, text)
    await rename(temporary, absolute)
  } catch (error) {
    await unlink(temporary).catch(() => {})
    throw error
  }
}

function timestampFileName(timestamp) {
  const match = /T(\d{2}):/.exec(timestamp)
  invariant(match, `timestamp cannot be converted to a cost filename: ${timestamp}`)
  return `cost-${match[1]}.json`
}

export async function writeServerBundle(bundleDirectory, bundle) {
  const absoluteDirectory = resolve(bundleDirectory)
  await mkdir(absoluteDirectory, { recursive: true })
  const topologySerialized = serializedDocument(bundle.topology)
  await writeTextAtomic(join(absoluteDirectory, 'topology.json'), topologySerialized.text)
  const costReferences = []
  const costFileNames = new Set()
  for (const slice of bundle.costSlices) {
    const file = timestampFileName(slice.timestamp)
    invariant(!costFileNames.has(file), `multiple timestamps resolve to the same cost filename: ${file}`)
    costFileNames.add(file)
    const serialized = serializedDocument(slice)
    await writeTextAtomic(join(absoluteDirectory, file), serialized.text)
    costReferences.push({
      timestamp: slice.timestamp,
      file,
      bytes: serialized.bytes,
      fileSha256: serialized.sha256,
      contentFingerprintSha256: slice.contentFingerprintSha256,
      statusCounts: slice.statusCounts,
    })
  }
  const manifest = {
    schemaVersion: bundle.schemaVersion ?? 'environment-cost-server-bundle-1.0',
    status: 'completed',
    ...bundle.manifestMetadata,
    counts: {
      nodeCount: bundle.topology.counts.nodeCount,
      physicalEdgeCount: bundle.topology.counts.physicalEdgeCount,
      directedEdgeCount: bundle.topology.counts.directedEdgeCount,
      hourCount: bundle.costSlices.length,
    },
    topology: {
      file: 'topology.json',
      bytes: topologySerialized.bytes,
      fileSha256: topologySerialized.sha256,
      contentFingerprintSha256: bundle.topology.contentFingerprintSha256,
    },
    costSlices: costReferences,
    diagnostics: bundle.diagnostics,
  }
  manifest.bundleFingerprintSha256 = sha256(JSON.stringify({
    inputs: manifest.inputs,
    topology: manifest.topology,
    costSlices: manifest.costSlices,
  }))
  const manifestSerialized = serializedDocument(manifest)
  await writeTextAtomic(join(absoluteDirectory, 'manifest.json'), manifestSerialized.text)
  return {
    manifest,
    manifestBytes: manifestSerialized.bytes,
    manifestFileSha256: manifestSerialized.sha256,
    totalBundleBytes: manifestSerialized.bytes + topologySerialized.bytes + costReferences.reduce((sum, item) => sum + item.bytes, 0),
  }
}

async function main() {
  const started = performance.now()
  const options = parseArgs(process.argv.slice(2))
  const [graphText, environmentText] = await Promise.all([
    readFile(resolve(options.graph), 'utf8'),
    readFile(resolve(options.environment), 'utf8'),
  ])
  const bundle = buildServerBundleDocuments(JSON.parse(graphText), JSON.parse(environmentText), {
    allowUnmatchedAsMissing: options.allowUnmatchedAsMissing,
    provenance: options.provenance,
  })
  const written = await writeServerBundle(options['bundle-directory'], bundle)
  const report = {
    schemaVersion: 'environment-cost-server-bundle-report-1.0',
    status: 'completed',
    generatedAt: written.manifest.dataset.generatedAt,
    graphPath: options.graph,
    environmentPath: options.environment,
    bundleDirectory: options['bundle-directory'],
    manifestPath: join(options['bundle-directory'], 'manifest.json').replaceAll('\\', '/'),
    bundleFingerprintSha256: written.manifest.bundleFingerprintSha256,
    totalBundleBytes: written.totalBundleBytes,
    totalSeconds: (performance.now() - started) / 1000,
    counts: written.manifest.counts,
    diagnostics: bundle.diagnostics,
  }
  await writeTextAtomic(options.report, `${JSON.stringify(report, null, 2)}\n`)
  console.log(`ENVIRONMENT_COST_SERVER_BUNDLE_BUILT area=${written.manifest.area.areaId} nodes=${written.manifest.counts.nodeCount} directedEdges=${written.manifest.counts.directedEdgeCount} hours=${written.manifest.counts.hourCount} bytes=${written.totalBundleBytes} fingerprint=${written.manifest.bundleFingerprintSha256}`)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message)
    console.error(usage())
    process.exitCode = 1
  })
}

export { CODE_TO_STATUS, STATUS_TO_CODE, validateCostSlice, validateTopology }
