#!/usr/bin/env node

import { createHash } from 'node:crypto'
import { createWriteStream } from 'node:fs'
import { mkdir, readFile, rename, stat, unlink, writeFile } from 'node:fs/promises'
import { once } from 'node:events'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { validateHourlyOutput } from '../hourly-environment-cost/validate-hourly-output.mjs'
import { geographicToUnityLocal, unityLocalToGeographic } from './japan-plane-rectangular.mjs'

const FORMULA_TOLERANCE_SECONDS = 1e-6

function usage() {
  return `Usage: node --max-old-space-size=8192 tools/environment-cost-network/build-environment-cost-road-network.mjs \\
  --graph <pedestrian-road-network.json> --environment <hourly-environment-cost.json> \\
  --output <environment-cost-road-network.json> --report <integration-report.json> \\
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
  for (const name of ['graph', 'environment', 'output', 'report']) {
    if (!options[name]) throw new Error(`--${name} is required`)
  }
  if (!['analysis', 'fixture'].includes(options.provenance)) throw new Error('--provenance must be analysis or fixture')
  return options
}

function invariant(condition, message) {
  if (!condition) throw new Error(message)
}

function* roadNetworkJsonChunks(document, omitContentFingerprint = false) {
  yield '{'
  const entries = Object.entries(document)
  for (let entryIndex = 0; entryIndex < entries.length; entryIndex += 1) {
    const [key, originalValue] = entries[entryIndex]
    if (entryIndex > 0) yield ','
    yield `${JSON.stringify(key)}:`
    let value = originalValue
    if (key === 'dataset' && omitContentFingerprint) {
      value = { ...originalValue }
      delete value.contentFingerprintSha256
    }
    if ((key === 'nodes' || key === 'edges') && Array.isArray(value)) {
      yield '['
      for (let index = 0; index < value.length; index += 1) {
        if (index > 0) yield ','
        yield JSON.stringify(value[index])
      }
      yield ']'
    } else {
      yield JSON.stringify(value)
    }
  }
  yield '}'
}

function roadNetworkFingerprint(document) {
  const hash = createHash('sha256')
  for (const chunk of roadNetworkJsonChunks(document, true)) hash.update(chunk)
  return hash.digest('hex')
}

function equalCoordinate(left, right, tolerance = 1e-10) {
  return Array.isArray(left) && Array.isArray(right) && left.length === 2 && right.length === 2 &&
    Math.abs(left[0] - right[0]) <= tolerance && Math.abs(left[1] - right[1]) <= tolerance
}

function validateGraph(graph) {
  invariant(graph?.schemaVersion === 'pedestrian-road-network-1.0', 'graph schemaVersion must be pedestrian-road-network-1.0')
  invariant(typeof graph.areaId === 'string' && graph.areaId.length > 0, 'graph areaId is missing')
  invariant(Array.isArray(graph.nodes) && graph.nodes.length >= 2, 'graph nodes are missing')
  invariant(Array.isArray(graph.edges) && graph.edges.length > 0, 'graph edges are missing')
  invariant(typeof graph.graphFingerprintSha256 === 'string' && /^[0-9a-f]{64}$/.test(graph.graphFingerprintSha256), 'graph fingerprint is invalid')

  const nodes = new Map()
  for (const node of graph.nodes) {
    invariant(typeof node.id === 'string' && !nodes.has(node.id), `duplicate or invalid graph node: ${node.id}`)
    invariant(Number.isInteger(node.osmNodeId), `graph node source ID is invalid: ${node.id}`)
    invariant(Array.isArray(node.coordinate) && node.coordinate.length === 2 && node.coordinate.every(Number.isFinite), `graph node coordinate is invalid: ${node.id}`)
    nodes.set(node.id, node)
  }
  const edgeIds = new Set()
  for (const edge of graph.edges) {
    invariant(typeof edge.id === 'string' && !edgeIds.has(edge.id), `duplicate or invalid graph edge: ${edge.id}`)
    edgeIds.add(edge.id)
    invariant(nodes.has(edge.fromNodeId) && nodes.has(edge.toNodeId), `graph edge references a missing node: ${edge.id}`)
    invariant(['forward', 'backward'].includes(edge.direction), `graph edge direction is invalid: ${edge.id}`)
    invariant(Array.isArray(edge.sourceEdgeIds) && edge.sourceEdgeIds.length > 0 && new Set(edge.sourceEdgeIds).size === edge.sourceEdgeIds.length, `graph sourceEdgeIds are invalid: ${edge.id}`)
    invariant(Array.isArray(edge.coordinates) && edge.coordinates.length >= 2, `graph geometry is missing: ${edge.id}`)
    invariant(equalCoordinate(edge.coordinates[0], nodes.get(edge.fromNodeId).coordinate), `graph geometry start mismatch: ${edge.id}`)
    invariant(equalCoordinate(edge.coordinates.at(-1), nodes.get(edge.toNodeId).coordinate), `graph geometry end mismatch: ${edge.id}`)
    invariant(Number.isFinite(edge.lengthMeters) && edge.lengthMeters > 0, `graph length is invalid: ${edge.id}`)
    invariant(Number.isFinite(edge.walkingSeconds) && edge.walkingSeconds > 0, `graph walking time is invalid: ${edge.id}`)
  }
}

function bbox(nodes) {
  let minLongitude = Infinity
  let minLatitude = Infinity
  let maxLongitude = -Infinity
  let maxLatitude = -Infinity
  for (const node of nodes) {
    minLongitude = Math.min(minLongitude, node.coordinate[0])
    minLatitude = Math.min(minLatitude, node.coordinate[1])
    maxLongitude = Math.max(maxLongitude, node.coordinate[0])
    maxLatitude = Math.max(maxLatitude, node.coordinate[1])
  }
  return [minLongitude, minLatitude, maxLongitude, maxLatitude]
}

function costDefinitions(maximumWalkingSeconds) {
  const maximumExposureSeconds = Math.max(3600, Math.ceil(maximumWalkingSeconds))
  return [
    {
      id: 'shadeRatio',
      displayName: '日陰',
      description: '道路辺の有効サンプルのうち建物によって日陰となった割合です。',
      unit: 'ratio',
      range: { min: 0, max: 1 },
      valueDirection: 'higher-is-better',
      routeAggregation: 'walking-time-weighted-mean',
      missingValuePolicy: 'preserve-null',
      presentation: {
        viewerMode: true,
        displayUnit: '%',
        displayScale: 100,
        valueDirectionLabel: '高いほど日陰が多い',
        sampleKpiLabel: '道路加重平均日陰率',
        colors: [
          { value: 0, color: '#f59e0b', label: '日向が多い' },
          { value: 0.5, color: '#84cc16', label: '中程度' },
          { value: 1, color: '#047857', label: '日陰が多い' },
        ],
      },
    },
    {
      id: 'solarExposureSeconds',
      displayName: '日射曝露時間',
      description: 'その道路辺を既定歩行速度で通過するときに日向となる推定時間です。',
      unit: 's',
      range: { min: 0, max: maximumExposureSeconds },
      valueDirection: 'higher-is-worse',
      routeAggregation: 'sum',
      missingValuePolicy: 'preserve-null',
      presentation: {
        viewerMode: false,
        displayUnit: '秒',
        displayScale: 1,
        valueDirectionLabel: '低いほど日射が少ない',
        sampleKpiLabel: '道路合計日射曝露時間',
        colors: [
          { value: 0, color: '#047857', label: '曝露なし' },
          { value: maximumExposureSeconds / 2, color: '#f59e0b', label: '中程度' },
          { value: maximumExposureSeconds, color: '#b91c1c', label: '曝露大' },
        ],
      },
    },
  ]
}

function missingTimeSlices(timestamps) {
  return timestamps.map((timestamp) => ({
    timestamp,
    status: 'missing',
    sampleCoverage: { sampleCount: 0, validSampleCount: 0, noGroundSampleCount: 0 },
    values: { shadeRatio: null, solarExposureSeconds: null },
  }))
}

function aggregateTimeSlices(graphEdge, sourceEdges, timestamps) {
  if (sourceEdges.length === 0) return missingTimeSlices(timestamps)
  return timestamps.map((timestamp, timestampIndex) => {
    const sourceSlices = sourceEdges.map((edge) => edge.hourly[timestampIndex])
    invariant(sourceSlices.every((slice) => slice.timestamp === timestamp), `source timestamp mismatch: ${graphEdge.id} ${timestamp}`)
    const sampleCount = sourceEdges.reduce((sum, edge) => sum + edge.sampleCount, 0)
    const validSampleCount = sourceEdges.reduce((sum, edge) => sum + edge.validSampleCount, 0)
    const noGroundSampleCount = sourceEdges.reduce((sum, edge) => sum + edge.noGroundSampleCount, 0)
    const calculated = sourceEdges.map((edge, index) => ({ edge, slice: sourceSlices[index] }))
      .filter(({ slice }) => Number.isFinite(slice.shadeRatio))
    if (validSampleCount === 0) {
      invariant(calculated.length === 0, `missing source coverage contains a calculated value: ${graphEdge.id} ${timestamp}`)
      return {
        timestamp,
        status: 'missing',
        sampleCoverage: { sampleCount, validSampleCount, noGroundSampleCount },
        values: { shadeRatio: null, solarExposureSeconds: null },
      }
    }
    invariant(calculated.length > 0, `contract v1 cannot represent a missing cost with valid road samples: ${graphEdge.id} ${timestamp}`)
    const representedValidSamples = calculated.reduce((sum, { edge }) => sum + edge.validSampleCount, 0)
    invariant(representedValidSamples === validSampleCount, `some valid source samples have no cost: ${graphEdge.id} ${timestamp}`)
    const shadeRatio = calculated.reduce((sum, { edge, slice }) => sum + slice.shadeRatio * edge.validSampleCount, 0) / validSampleCount
    const solarExposureSeconds = graphEdge.walkingSeconds * (1 - shadeRatio)
    invariant(Math.abs(solarExposureSeconds - graphEdge.walkingSeconds * (1 - shadeRatio)) <= FORMULA_TOLERANCE_SECONDS, `solar exposure formula mismatch: ${graphEdge.id} ${timestamp}`)
    return {
      timestamp,
      status: noGroundSampleCount === 0 ? 'available' : 'partial',
      sampleCoverage: { sampleCount, validSampleCount, noGroundSampleCount },
      values: { shadeRatio, solarExposureSeconds },
    }
  })
}

function coordinateRoundTrip(graph) {
  const zoneId = graph.coordinateSystems?.unity?.japanPlaneRectangularZoneId
  const reference = graph.coordinateSystems?.unity?.referencePointGeographic
  invariant(Number.isInteger(zoneId) && zoneId >= 1 && zoneId <= 19, 'graph Unity coordinate zone is invalid')
  invariant(equalCoordinate(reference, graph.extent.center), 'graph Unity reference point must equal the area center')
  const sortedNodes = [...graph.nodes].sort((left, right) => left.id.localeCompare(right.id))
  const indexes = [...new Set([0, Math.floor(sortedNodes.length / 2), sortedNodes.length - 1])]
  let maximumLongitudeErrorDegrees = 0
  let maximumLatitudeErrorDegrees = 0
  for (const index of indexes) {
    const coordinate = sortedNodes[index].coordinate
    const local = geographicToUnityLocal(coordinate, reference, zoneId)
    const restored = unityLocalToGeographic(local, reference, zoneId)
    maximumLongitudeErrorDegrees = Math.max(maximumLongitudeErrorDegrees, Math.abs(restored[0] - coordinate[0]))
    maximumLatitudeErrorDegrees = Math.max(maximumLatitudeErrorDegrees, Math.abs(restored[1] - coordinate[1]))
  }
  invariant(maximumLongitudeErrorDegrees <= 1e-9 && maximumLatitudeErrorDegrees <= 1e-9, 'Unity/geographic coordinate round trip exceeded tolerance')
  return { testedPointCount: indexes.length, maximumLongitudeErrorDegrees, maximumLatitudeErrorDegrees }
}

export function buildEnvironmentCostRoadNetwork(graph, environment, options = {}) {
  validateGraph(graph)
  const environmentSummary = validateHourlyOutput(environment)
  invariant(graph.areaId === environment.areaId, 'graph and environment areaId do not match')
  invariant(equalCoordinate(graph.extent.center, environment.center), 'graph and environment centers do not match')
  invariant(Math.abs(graph.extent.radiusMeters - environment.radiusMeters) <= 1e-9, 'graph and environment radii do not match')
  invariant(graph.coordinateSystems.unity.japanPlaneRectangularZoneId === environment.coordinateZoneId, 'graph and environment coordinate zones do not match')
  invariant(Math.abs(graph.walking.defaultSpeedMetersPerSecond - environment.settings.walkingSpeedMetersPerSecond) <= 1e-12, 'graph and environment walking speeds do not match')

  const sourceCosts = new Map(environment.edges.map((edge) => [edge.id, edge]))
  const graphSourceIds = new Set(graph.edges.flatMap((edge) => edge.sourceEdgeIds))
  const timestamps = environment.edges[0].hourly.map((slice) => slice.timestamp)
  const partiallyMatchedEdges = []
  const unmatchedPhysicalEdgeIds = new Set()
  const matchedPhysicalEdgeIds = new Set()
  const outputEdges = [...graph.edges].sort((left, right) => left.id.localeCompare(right.id)).map((edge) => {
    const matches = edge.sourceEdgeIds.map((id) => sourceCosts.get(id)).filter(Boolean)
    if (matches.length > 0 && matches.length !== edge.sourceEdgeIds.length) partiallyMatchedEdges.push(edge.id)
    if (matches.length === 0) unmatchedPhysicalEdgeIds.add(edge.physicalEdgeId)
    else matchedPhysicalEdgeIds.add(edge.physicalEdgeId)
    return {
      id: edge.id,
      physicalEdgeId: edge.physicalEdgeId,
      sourceEdgeIds: [...edge.sourceEdgeIds].sort(),
      fromNodeId: edge.fromNodeId,
      toNodeId: edge.toNodeId,
      direction: edge.direction,
      geometry: { type: 'LineString', coordinates: edge.coordinates },
      lengthMeters: edge.lengthMeters,
      walkingSeconds: edge.walkingSeconds,
      timeSlices: aggregateTimeSlices(edge, matches, timestamps),
    }
  })
  invariant(partiallyMatchedEdges.length === 0, `graph edges are only partially represented by environment costs: ${partiallyMatchedEdges.slice(0, 10).join(', ')}`)
  if (unmatchedPhysicalEdgeIds.size > 0 && !options.allowUnmatchedAsMissing) {
    throw new Error(`${unmatchedPhysicalEdgeIds.size} physical graph edges have no environment-cost source; pass --allow-unmatched-as-missing to preserve them as explicit missing values`)
  }

  const outputNodes = [...graph.nodes].sort((left, right) => left.id.localeCompare(right.id)).map((node) => ({
    id: node.id,
    sourceNodeId: node.osmNodeId,
    coordinate: node.coordinate,
  }))
  const coordinateVerification = coordinateRoundTrip(graph)
  const maximumWalkingSeconds = outputEdges.reduce((maximum, edge) => Math.max(maximum, edge.walkingSeconds), 0)
  const statusCountByTimestamp = Object.fromEntries(timestamps.map((timestamp) => [timestamp, { available: 0, partial: 0, missing: 0 }]))
  for (const edge of outputEdges) {
    for (const slice of edge.timeSlices) statusCountByTimestamp[slice.timestamp][slice.status] += 1
  }
  const provenance = options.provenance ?? 'analysis'
  invariant(['analysis', 'fixture'].includes(provenance), 'provenance must be analysis or fixture')
  const document = {
    schemaVersion: 'environment-cost-road-network-1.0',
    dataset: {
      id: `${graph.areaId}-environment-cost-road-network-v1`,
      name: `${graph.areaId} 時間別環境コスト道路ネットワーク`,
      provenance,
      generatedAt: environment.generatedAt,
      notice: provenance === 'fixture'
        ? 'Viewer統合テスト用の小型架空データです。実際の環境評価や避難判断には使用できません。'
        : 'PLATEAU建物LOD1とOpenStreetMap道路による事前解析値です。欠測値を0として扱わないでください。',
    },
    area: {
      areaId: graph.areaId,
      center: graph.extent.center,
      radiusMeters: graph.extent.radiusMeters,
      bbox: bbox(outputNodes),
    },
    coordinateReferenceSystem: {
      geometryEpsg: 4326,
      axisOrder: ['longitude', 'latitude'],
      unity: {
        projectedEpsg: 6668 + environment.coordinateZoneId,
        coordinateZoneId: environment.coordinateZoneId,
        axisConvention: 'EUN',
        referencePointGeographic: graph.extent.center,
      },
    },
    scenario: {
      id: `${graph.areaId}-${environment.settings.date}-hourly`,
      referenceDate: environment.settings.date,
      timezone: environment.settings.timezone,
      availableTimestamps: timestamps,
      defaultTimestamp: timestamps[Math.floor((timestamps.length - 1) / 2)],
      timeSelectionPolicy: 'nearest-on-reference-date-ties-earlier',
    },
    costDefinitions: costDefinitions(maximumWalkingSeconds),
    nodes: outputNodes,
    edges: outputEdges,
    extensions: {
      integration: {
        roadGraphSchemaVersion: graph.schemaVersion,
        roadGraphFingerprintSha256: graph.graphFingerprintSha256,
        environmentCostSchemaVersion: environment.schemaVersion,
        environmentCostAnalysisKey: environment.analysisKey,
        environmentCostFingerprintSha256: environment.resultFingerprintSha256,
        sourceEdgeAggregation: 'valid-sample-weighted-mean',
        unmatchedGraphPolicy: options.allowUnmatchedAsMissing ? 'preserve-as-missing' : 'fail',
        matchedPhysicalEdgeCount: matchedPhysicalEdgeIds.size,
        unmatchedPhysicalEdgeCount: unmatchedPhysicalEdgeIds.size,
        unmatchedPhysicalEdgeIds: [...unmatchedPhysicalEdgeIds].sort(),
        ignoredEnvironmentSourceEdgeCount: environment.edges.filter((edge) => !graphSourceIds.has(edge.id)).length,
        coordinateRoundTrip: coordinateVerification,
      },
    },
  }
  document.dataset.contentFingerprintSha256 = roadNetworkFingerprint(document)
  return {
    document,
    diagnostics: {
      sourceGraphNodeCount: graph.nodes.length,
      sourceGraphDirectedEdgeCount: graph.edges.length,
      sourceGraphPhysicalEdgeCount: new Set(graph.edges.map((edge) => edge.physicalEdgeId)).size,
      sourceEnvironmentEdgeCount: environmentSummary.edgeCount,
      outputNodeCount: outputNodes.length,
      outputDirectedEdgeCount: outputEdges.length,
      matchedPhysicalEdgeCount: matchedPhysicalEdgeIds.size,
      unmatchedPhysicalEdgeCount: unmatchedPhysicalEdgeIds.size,
      partiallyMatchedDirectedEdgeCount: partiallyMatchedEdges.length,
      ignoredEnvironmentSourceEdgeCount: document.extensions.integration.ignoredEnvironmentSourceEdgeCount,
      hourCount: timestamps.length,
      totalTimeSliceCount: outputEdges.length * timestamps.length,
      statusCountByTimestamp,
      coordinateVerification,
    },
  }
}

async function writeJsonAtomic(path, document, pretty = false) {
  const absolute = resolve(path)
  await mkdir(dirname(absolute), { recursive: true })
  const temporary = `${absolute}.partial`
  try {
    await writeFile(temporary, `${JSON.stringify(document, null, pretty ? 2 : undefined)}\n`)
    await rename(temporary, absolute)
  } catch (error) {
    await unlink(temporary).catch(() => {})
    throw error
  }
}

async function writeRoadNetworkAtomic(path, document) {
  const absolute = resolve(path)
  await mkdir(dirname(absolute), { recursive: true })
  const temporary = `${absolute}.partial`
  const stream = createWriteStream(temporary, { encoding: 'utf8' })
  try {
    let buffer = ''
    for (const chunk of roadNetworkJsonChunks(document)) {
      buffer += chunk
      if (buffer.length < 1024 * 1024) continue
      if (!stream.write(buffer)) await once(stream, 'drain')
      buffer = ''
    }
    buffer += '\n'
    if (buffer.length > 0 && !stream.write(buffer)) await once(stream, 'drain')
    stream.end()
    await once(stream, 'finish')
    await rename(temporary, absolute)
  } catch (error) {
    stream.destroy()
    await unlink(temporary).catch(() => {})
    throw error
  }
}

async function main() {
  const started = performance.now()
  const options = parseArgs(process.argv.slice(2))
  const [graphText, environmentText] = await Promise.all([
    readFile(resolve(options.graph), 'utf8'),
    readFile(resolve(options.environment), 'utf8'),
  ])
  const { document, diagnostics } = buildEnvironmentCostRoadNetwork(JSON.parse(graphText), JSON.parse(environmentText), {
    allowUnmatchedAsMissing: options.allowUnmatchedAsMissing,
    provenance: options.provenance,
  })
  const { validateDocument } = await import('../../viewer/scripts/validate-environment-cost-data.mjs')
  const validationErrors = validateDocument(document)
  if (validationErrors.length > 0) throw new Error(`formal contract validation failed: ${validationErrors.slice(0, 20).join('; ')}`)
  await writeRoadNetworkAtomic(options.output, document)
  const outputBytes = (await stat(resolve(options.output))).size
  const report = {
    schemaVersion: 'environment-cost-network-integration-report-1.0',
    status: 'completed',
    generatedAt: document.dataset.generatedAt,
    graphPath: options.graph,
    environmentPath: options.environment,
    outputPath: options.output,
    outputBytes,
    contentFingerprintSha256: document.dataset.contentFingerprintSha256,
    totalSeconds: (performance.now() - started) / 1000,
    validation: { schemaAndSemanticsValid: true, ...diagnostics },
  }
  await writeJsonAtomic(options.report, report, true)
  console.log(`ENVIRONMENT_COST_NETWORK_BUILT area=${document.area.areaId} nodes=${diagnostics.outputNodeCount} directedEdges=${diagnostics.outputDirectedEdgeCount} hours=${diagnostics.hourCount} unmatchedPhysicalEdges=${diagnostics.unmatchedPhysicalEdgeCount} bytes=${outputBytes} fingerprint=${document.dataset.contentFingerprintSha256}`)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message)
    console.error(usage())
    process.exitCode = 1
  })
}

export { aggregateTimeSlices, coordinateRoundTrip, equalCoordinate, validateGraph }
