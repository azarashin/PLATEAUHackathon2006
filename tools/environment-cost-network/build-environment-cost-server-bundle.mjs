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
  invariant(topology.schemaVersion === 'environment-cost-server-topology-1.0', 'server topology schemaVersion is invalid')
  invariant(topology.nodes.length === topology.counts.nodeCount, 'server topology node count mismatch')
  invariant(topology.physicalEdges.length === topology.counts.physicalEdgeCount, 'server topology physical edge count mismatch')
  invariant(topology.directedEdges.length === topology.counts.directedEdgeCount, 'server topology directed edge count mismatch')
  invariant(topology.contentFingerprintSha256 === contentFingerprint(topology), 'server topology content fingerprint mismatch')
  const nodeSourceIds = new Set()
  for (const [sourceNodeId, longitude, latitude] of topology.nodes) {
    invariant(Number.isSafeInteger(sourceNodeId) && !nodeSourceIds.has(sourceNodeId), `invalid or duplicate server node: ${sourceNodeId}`)
    nodeSourceIds.add(sourceNodeId)
    invariant(Number.isFinite(longitude) && longitude >= -180 && longitude <= 180 && Number.isFinite(latitude) && latitude >= -90 && latitude <= 90, `invalid server node coordinate: ${sourceNodeId}`)
  }
  const physicalIds = new Set()
  for (const [physicalEdgeId, sourceEdgeIds] of topology.physicalEdges) {
    invariant(typeof physicalEdgeId === 'string' && !physicalIds.has(physicalEdgeId), `invalid or duplicate physical edge: ${physicalEdgeId}`)
    physicalIds.add(physicalEdgeId)
    invariant(Array.isArray(sourceEdgeIds) && sourceEdgeIds.length > 0 && new Set(sourceEdgeIds).size === sourceEdgeIds.length, `invalid source edges: ${physicalEdgeId}`)
  }
  for (let index = 0; index < topology.directedEdges.length; index += 1) {
    const [physicalIndex, fromNodeIndex, toNodeIndex, directionCode, lengthMeters, walkingSeconds] = topology.directedEdges[index]
    invariant(Number.isInteger(physicalIndex) && physicalIndex >= 0 && physicalIndex < topology.physicalEdges.length, `invalid directed physical index: ${index}`)
    invariant(Number.isInteger(fromNodeIndex) && fromNodeIndex >= 0 && fromNodeIndex < topology.nodes.length, `invalid directed from-node index: ${index}`)
    invariant(Number.isInteger(toNodeIndex) && toNodeIndex >= 0 && toNodeIndex < topology.nodes.length, `invalid directed to-node index: ${index}`)
    invariant(fromNodeIndex !== toNodeIndex, `self-loop directed edge: ${index}`)
    invariant(directionCode === 0 || directionCode === 1, `invalid directed edge direction code: ${index}`)
    invariant(Number.isFinite(lengthMeters) && lengthMeters > 0 && Number.isFinite(walkingSeconds) && walkingSeconds > 0, `invalid directed edge measurement: ${index}`)
  }
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
  invariant(slice.schemaVersion === 'environment-cost-server-cost-slice-1.0', 'server cost slice schemaVersion is invalid')
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
    schemaVersion: 'environment-cost-server-bundle-1.0',
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
