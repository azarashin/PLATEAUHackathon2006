import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { dirname, resolve, sep } from 'node:path'
import { CODE_TO_STATUS, validateCostSlice, validateTopology } from './build-environment-cost-server-bundle.mjs'

function invariant(condition, message) {
  if (!condition) throw new Error(message)
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex')
}

function validateManifest(manifest) {
  invariant(manifest?.schemaVersion === 'environment-cost-server-bundle-1.0', 'server bundle manifest schemaVersion is invalid')
  invariant(manifest.status === 'completed', 'server bundle manifest is not completed')
  invariant(typeof manifest.bundleFingerprintSha256 === 'string' && /^[0-9a-f]{64}$/.test(manifest.bundleFingerprintSha256), 'server bundle fingerprint is invalid')
  invariant(Array.isArray(manifest.scenario?.availableTimestamps) && manifest.scenario.availableTimestamps.length > 0, 'server bundle timestamps are missing')
  invariant(new Set(manifest.scenario.availableTimestamps).size === manifest.scenario.availableTimestamps.length, 'server bundle timestamps are duplicated')
  invariant(manifest.scenario.availableTimestamps.includes(manifest.scenario.defaultTimestamp), 'server bundle default timestamp is not available')
  invariant(manifest.counts?.hourCount === manifest.scenario.availableTimestamps.length, 'server bundle hour count mismatch')
  invariant(Array.isArray(manifest.costSlices) && manifest.costSlices.length === manifest.counts.hourCount, 'server bundle cost references mismatch')
  invariant(
    JSON.stringify(manifest.costSlices.map((reference) => reference.timestamp)) === JSON.stringify(manifest.scenario.availableTimestamps),
    'server bundle cost timestamps do not match the scenario',
  )
  const referencedFiles = [manifest.topology?.file, ...manifest.costSlices.map((reference) => reference.file)]
  invariant(referencedFiles.every((file) => typeof file === 'string' && file.length > 0), 'server bundle contains an invalid file reference')
  invariant(new Set(referencedFiles).size === referencedFiles.length, 'server bundle file references are duplicated')
  const fingerprint = sha256(JSON.stringify({
    inputs: manifest.inputs,
    topology: manifest.topology,
    costSlices: manifest.costSlices,
  }))
  invariant(fingerprint === manifest.bundleFingerprintSha256, 'server bundle manifest fingerprint mismatch')
}

export function safeReferencedPath(directory, file) {
  invariant(typeof file === 'string' && file.length > 0, 'server bundle file reference is invalid')
  const path = resolve(directory, file)
  const root = resolve(directory)
  invariant(path === root || path.startsWith(`${root}${sep}`), `server bundle file escapes its directory: ${file}`)
  return path
}

async function readVerifiedJson(directory, reference) {
  const path = safeReferencedPath(directory, reference.file)
  const bytes = await readFile(path)
  invariant(bytes.length === reference.bytes, `server bundle file size mismatch: ${reference.file}`)
  invariant(sha256(bytes) === reference.fileSha256, `server bundle file hash mismatch: ${reference.file}`)
  return JSON.parse(bytes.toString('utf8'))
}

function buildRuntimeTopology(topology) {
  const nodeCount = topology.nodes.length
  const directedEdgeCount = topology.directedEdges.length
  const nodeSourceIds = new Float64Array(nodeCount)
  const nodeLongitudes = new Float64Array(nodeCount)
  const nodeLatitudes = new Float64Array(nodeCount)
  for (let index = 0; index < nodeCount; index += 1) {
    nodeSourceIds[index] = topology.nodes[index][0]
    nodeLongitudes[index] = topology.nodes[index][1]
    nodeLatitudes[index] = topology.nodes[index][2]
  }
  const directedPhysicalIndexes = new Uint32Array(directedEdgeCount)
  const directedFromNodeIndexes = new Uint32Array(directedEdgeCount)
  const directedToNodeIndexes = new Uint32Array(directedEdgeCount)
  const directedDirectionCodes = new Uint8Array(directedEdgeCount)
  const directedLengthMeters = new Float64Array(directedEdgeCount)
  const directedWalkingSeconds = new Float64Array(directedEdgeCount)
  for (let index = 0; index < directedEdgeCount; index += 1) {
    const edge = topology.directedEdges[index]
    directedPhysicalIndexes[index] = edge[0]
    directedFromNodeIndexes[index] = edge[1]
    directedToNodeIndexes[index] = edge[2]
    directedDirectionCodes[index] = edge[3]
    directedLengthMeters[index] = edge[4]
    directedWalkingSeconds[index] = edge[5]
  }
  return {
    nodeSourceIds,
    nodeLongitudes,
    nodeLatitudes,
    physicalEdgeIds: topology.physicalEdges.map((edge) => edge[0]),
    physicalSourceEdgeIds: topology.physicalEdges.map((edge) => edge[1]),
    directedPhysicalIndexes,
    directedFromNodeIndexes,
    directedToNodeIndexes,
    directedDirectionCodes,
    directedLengthMeters,
    directedWalkingSeconds,
  }
}

function buildRuntimeCosts(slice) {
  const count = slice.costs.length
  const statuses = new Uint8Array(count)
  const sampleCounts = new Uint32Array(count)
  const validSampleCounts = new Uint32Array(count)
  const noGroundSampleCounts = new Uint32Array(count)
  const shadeRatios = new Float64Array(count)
  const solarExposureSeconds = new Float64Array(count)
  for (let index = 0; index < count; index += 1) {
    const cost = slice.costs[index]
    statuses[index] = cost[0]
    sampleCounts[index] = cost[1]
    validSampleCounts[index] = cost[2]
    noGroundSampleCounts[index] = cost[3]
    shadeRatios[index] = cost[4] ?? Number.NaN
    solarExposureSeconds[index] = cost[5] ?? Number.NaN
  }
  return { statuses, sampleCounts, validSampleCounts, noGroundSampleCounts, shadeRatios, solarExposureSeconds }
}

export async function loadEnvironmentCostServerBundle(manifestPath, options = {}) {
  const absoluteManifestPath = resolve(manifestPath)
  const directory = dirname(absoluteManifestPath)
  const manifest = JSON.parse(await readFile(absoluteManifestPath, 'utf8'))
  validateManifest(manifest)
  const topology = await readVerifiedJson(directory, manifest.topology)
  validateTopology(topology)
  invariant(topology.contentFingerprintSha256 === manifest.topology.contentFingerprintSha256, 'manifest and topology content fingerprints differ')
  invariant(topology.areaId === manifest.area.areaId, 'manifest and topology area IDs differ')
  invariant(topology.graphFingerprintSha256 === manifest.inputs.roadGraphFingerprintSha256, 'manifest and topology graph fingerprints differ')
  invariant(topology.counts.nodeCount === manifest.counts.nodeCount && topology.counts.physicalEdgeCount === manifest.counts.physicalEdgeCount && topology.counts.directedEdgeCount === manifest.counts.directedEdgeCount, 'manifest and topology counts differ')

  const selectedTimestamps = options.timestamps ?? manifest.scenario.availableTimestamps
  invariant(Array.isArray(selectedTimestamps) && selectedTimestamps.length > 0, 'at least one timestamp must be loaded')
  const references = new Map(manifest.costSlices.map((reference) => [reference.timestamp, reference]))
  const walkingByPhysical = new Array(topology.physicalEdges.length)
  for (const [physicalIndex, , , , , walkingSeconds] of topology.directedEdges) {
    if (walkingByPhysical[physicalIndex] === undefined) walkingByPhysical[physicalIndex] = walkingSeconds
    else invariant(Math.abs(walkingByPhysical[physicalIndex] - walkingSeconds) <= 1e-9, `walking time differs by direction: ${physicalIndex}`)
  }
  const costsByTimestamp = new Map()
  for (const timestamp of selectedTimestamps) {
    const reference = references.get(timestamp)
    invariant(reference, `requested timestamp is not in the server bundle: ${timestamp}`)
    const slice = await readVerifiedJson(directory, reference)
    validateCostSlice(slice, topology, walkingByPhysical)
    invariant(slice.timestamp === reference.timestamp, `manifest and cost timestamps differ: ${timestamp}`)
    invariant(slice.environmentCostFingerprintSha256 === manifest.inputs.environmentCostFingerprintSha256, `manifest and cost input fingerprints differ: ${timestamp}`)
    invariant(JSON.stringify(slice.statusCounts) === JSON.stringify(reference.statusCounts), `manifest and cost status counts differ: ${timestamp}`)
    invariant(slice.contentFingerprintSha256 === reference.contentFingerprintSha256, `manifest and cost content fingerprints differ: ${timestamp}`)
    costsByTimestamp.set(timestamp, buildRuntimeCosts(slice))
  }
  const runtime = buildRuntimeTopology(topology)
  return {
    manifest,
    ...runtime,
    costsByTimestamp,
    nodeId(nodeIndex) {
      invariant(Number.isInteger(nodeIndex) && nodeIndex >= 0 && nodeIndex < runtime.nodeSourceIds.length, `node index is out of range: ${nodeIndex}`)
      return `osm-node-${runtime.nodeSourceIds[nodeIndex]}`
    },
    directedEdgeId(edgeIndex) {
      invariant(Number.isInteger(edgeIndex) && edgeIndex >= 0 && edgeIndex < runtime.directedPhysicalIndexes.length, `directed edge index is out of range: ${edgeIndex}`)
      const physicalId = runtime.physicalEdgeIds[runtime.directedPhysicalIndexes[edgeIndex]]
      return `${physicalId}:${runtime.directedDirectionCodes[edgeIndex] === 0 ? 'forward' : 'backward'}`
    },
    directedEdgeGeometry(edgeIndex) {
      invariant(Number.isInteger(edgeIndex) && edgeIndex >= 0 && edgeIndex < runtime.directedPhysicalIndexes.length, `directed edge index is out of range: ${edgeIndex}`)
      const from = runtime.directedFromNodeIndexes[edgeIndex]
      const to = runtime.directedToNodeIndexes[edgeIndex]
      return [
        [runtime.nodeLongitudes[from], runtime.nodeLatitudes[from]],
        [runtime.nodeLongitudes[to], runtime.nodeLatitudes[to]],
      ]
    },
    directedEdgeCost(edgeIndex, timestamp) {
      invariant(Number.isInteger(edgeIndex) && edgeIndex >= 0 && edgeIndex < runtime.directedPhysicalIndexes.length, `directed edge index is out of range: ${edgeIndex}`)
      const costs = costsByTimestamp.get(timestamp)
      invariant(costs, `timestamp was not loaded: ${timestamp}`)
      const physicalIndex = runtime.directedPhysicalIndexes[edgeIndex]
      const shadeRatio = costs.shadeRatios[physicalIndex]
      const exposure = costs.solarExposureSeconds[physicalIndex]
      return {
        status: CODE_TO_STATUS[costs.statuses[physicalIndex]],
        sampleCount: costs.sampleCounts[physicalIndex],
        validSampleCount: costs.validSampleCounts[physicalIndex],
        noGroundSampleCount: costs.noGroundSampleCounts[physicalIndex],
        shadeRatio: Number.isNaN(shadeRatio) ? null : shadeRatio,
        solarExposureSeconds: Number.isNaN(exposure) ? null : exposure,
      }
    },
  }
}
