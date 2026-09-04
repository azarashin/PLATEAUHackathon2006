import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { buildServerBundleDocuments, writeServerBundle, writeV2ServerBundleFromRuntimeFile } from './build-environment-cost-server-bundle.mjs'
import { createFixtureInputs } from './fixture-inputs.mjs'
import { loadEnvironmentCostServerBundle, safeReferencedPath } from './load-environment-cost-server-bundle.mjs'
import { validateBundle } from '../../viewer/scripts/validate-environment-cost-server-bundle.mjs'
import { RouteService } from '../../server/src/route-service.mjs'

const inputs = createFixtureInputs()
assert.throws(
  () => buildServerBundleDocuments(inputs.graph, inputs.environment),
  /physical graph edges have no environment-cost source/,
)
const bundle = buildServerBundleDocuments(inputs.graph, inputs.environment, {
  allowUnmatchedAsMissing: true,
  provenance: 'fixture',
})
assert.equal(bundle.topology.nodes.length, 3)
assert.equal(bundle.topology.physicalEdges.length, 2)
assert.equal(bundle.topology.directedEdges.length, 3)
assert.equal(bundle.costSlices.length, 2)
assert.deepEqual(bundle.manifestMetadata.policyScenario, { id: 'baseline', label: '現状', fingerprintSha256: inputs.environment.resultFingerprintSha256 })
assert.equal(bundle.costSlices[0].costs.length, 2, 'costs must be stored once per physical edge, not once per direction')
assert.deepEqual(bundle.costSlices[0].costs[0], [1, 3, 2, 1, 0.25, 75])
assert.deepEqual(bundle.costSlices[0].costs[1], [0, 0, 0, 0, null, null])

const reorderedInputs = createFixtureInputs()
reorderedInputs.graph.nodes.reverse()
reorderedInputs.graph.edges.reverse()
reorderedInputs.environment.edges.reverse()
const reordered = buildServerBundleDocuments(reorderedInputs.graph, reorderedInputs.environment, {
  allowUnmatchedAsMissing: true,
  provenance: 'fixture',
})
assert.equal(reordered.topology.contentFingerprintSha256, bundle.topology.contentFingerprintSha256)
assert.deepEqual(
  reordered.costSlices.map((slice) => slice.contentFingerprintSha256),
  bundle.costSlices.map((slice) => slice.contentFingerprintSha256),
)

const v2Graph = {
  schemaVersion: 'environment-cost-pedestrian-network-2.0', areaId: inputs.graph.areaId, generatedAt: inputs.graph.generatedAt,
  graphFingerprintSha256: '4'.repeat(64), extent: inputs.graph.extent,
  coordinateReferenceSystem: { geographic: 'EPSG:4326', projected: 'EPSG:6677' },
  nodes: [
    { id: 'ped:1', coordinate: [139.7357, 35.6902] }, { id: 'ped:2', coordinate: [139.7360, 35.6904] }, { id: 'ped:3', coordinate: [139.7363, 35.6906] },
  ],
  physicalEdges: [
    { id: 'ped:one', fromNodeId: 'ped:1', toNodeId: 'ped:2', geometry: [[139.7357, 35.6902], [139.73585, 35.69035], [139.7360, 35.6904]], lengthMeters: 140, walkingSeconds: 100, facility: 'sidewalk', side: 'left', level: 0, fallback: false, source: { confidence: 'explicit' } },
    { id: 'ped:two', fromNodeId: 'ped:2', toNodeId: 'ped:3', geometry: [[139.7360, 35.6904], [139.7363, 35.6906]], lengthMeters: 70, walkingSeconds: 50, facility: 'footway', side: 'none', level: 0, fallback: false, source: { confidence: 'explicit' } },
  ],
  edges: [
    { id: 'ped:one:forward', physicalEdgeId: 'ped:one', fromNodeId: 'ped:1', toNodeId: 'ped:2', walkingSeconds: 100 }, { id: 'ped:one:backward', physicalEdgeId: 'ped:one', fromNodeId: 'ped:2', toNodeId: 'ped:1', walkingSeconds: 100 }, { id: 'ped:two:forward', physicalEdgeId: 'ped:two', fromNodeId: 'ped:2', toNodeId: 'ped:3', walkingSeconds: 50 },
  ],
}
const runtimeEdge = (id, walkingSeconds, shadeRatio) => ({ id, hourly: inputs.environment.settings.hours.map((hour, index) => ({ hour, timestamp: inputs.environment.edges[0].hourly[index].timestamp, status: 'available', exclusionReason: null, shadeRatio, solarExposureSeconds: walkingSeconds * (1 - shadeRatio), sampleCount: 2, validSampleCount: 2, noGroundSampleCount: 0 })) })
const v2Environment = {
  schemaVersion: 'environment-cost-runtime-shade-result-0.1', status: 'completed', areaId: inputs.graph.areaId, generatedAtUtc: inputs.graph.generatedAt,
  provenance: { center: inputs.graph.extent.center, radiusMeters: inputs.graph.extent.radiusMeters, analysisDate: '2025-08-01', timezone: 'Asia/Tokyo', hours: inputs.environment.settings.hours, graphFingerprintSha256: v2Graph.graphFingerprintSha256, networkQuality: { qualityContractVersion: 'pedestrian-network-safety-1.1', status: 'accepted', explicitOrDerivedRatio: 1, fallbackRatio: 0, sourceSchemaVersion: '0.2', validationFailures: [], validationWarnings: [] }, scenarioId: 'policy', policyFingerprintSha256: '6'.repeat(64), resultFingerprintSha256: '5'.repeat(64) },
  edges: [runtimeEdge('ped:one', 100, .25), runtimeEdge('ped:two', 50, .5)],
}
const v2Bundle = buildServerBundleDocuments(v2Graph, v2Environment, { provenance: 'fixture' })
assert.equal(v2Bundle.schemaVersion, 'environment-cost-server-bundle-2.0')
assert.equal(v2Bundle.topology.nodes[0][0], 'ped:1', 'v2 must preserve non-OSM string node IDs')
assert.equal(v2Bundle.topology.physicalEdges[0][3].length, 3, 'v2 must preserve complete physical geometry')
const reorderedV2Environment = { ...v2Environment, edges: [...v2Environment.edges].reverse() }
assert.equal(buildServerBundleDocuments(v2Graph, reorderedV2Environment, { provenance: 'fixture' }).topology.contentFingerprintSha256, v2Bundle.topology.contentFingerprintSha256, 'v2 environment edge order must not affect the bundle')
const duplicateV2Environment = { ...v2Environment, edges: [v2Environment.edges[0], { ...v2Environment.edges[0] }] }
assert.throws(() => buildServerBundleDocuments(v2Graph, duplicateV2Environment), /IDs do not exactly match|duplicated/, 'v2 must reject duplicate result IDs that hide a missing physical edge')
const unverifiedV2Environment = { ...v2Environment, provenance: { ...v2Environment.provenance, networkQuality: { ...v2Environment.provenance.networkQuality, status: 'unverified', fallbackRatio: -1 } } }
assert.throws(() => buildServerBundleDocuments(v2Graph, unverifiedV2Environment), /network quality is not accepted/, 'v2 must reject an unverified sidewalk network instead of emitting a route bundle')
const legacyV2Environment = { ...v2Environment, provenance: { ...v2Environment.provenance, networkQuality: { ...v2Environment.provenance.networkQuality, qualityContractVersion: undefined } } }
assert.throws(() => buildServerBundleDocuments(v2Graph, legacyV2Environment), /legacy-unverified/, 'v2 must reject the retired 80\/20 quality contract')
const safeLowExplicitV2Environment = { ...v2Environment, provenance: { ...v2Environment.provenance, networkQuality: { ...v2Environment.provenance.networkQuality, explicitOrDerivedRatio: .15, fallbackRatio: .85 } } }
assert.equal(buildServerBundleDocuments(v2Graph, safeLowExplicitV2Environment).topology.networkQuality.status, 'accepted', 'shared-road ratio must be diagnostic, not a safety rejection')
const arbitraryDirectedIds = { ...v2Graph, edges: v2Graph.edges.map((edge, index) => ({ ...edge, id: `edge-${index}` })) }
assert.equal(buildServerBundleDocuments(arbitraryDirectedIds, v2Environment, { provenance: 'fixture' }).topology.directedEdges[1][3], 1, 'v2 direction must derive from endpoints, not an ID suffix')
const mismatchedDirectedEndpoints = { ...v2Graph, edges: [{ ...v2Graph.edges[0], toNodeId: 'ped:3' }, ...v2Graph.edges.slice(1)] }
assert.throws(() => buildServerBundleDocuments(mismatchedDirectedEndpoints, v2Environment), /does not follow physical edge direction/, 'v2 must reject directed edges whose endpoints do not match their physical edge')

const directory = await mkdtemp(join(tmpdir(), 'environment-cost-server-bundle-'))
try {
  assert.throws(() => safeReferencedPath(directory, '../outside.json'), /escapes its directory/)
  const firstWrite = await writeServerBundle(directory, bundle)
  const secondWrite = await writeServerBundle(directory, reordered)
  assert.equal(secondWrite.manifest.bundleFingerprintSha256, firstWrite.manifest.bundleFingerprintSha256)
  assert.equal(secondWrite.totalBundleBytes, firstWrite.totalBundleBytes)

  const runtime = await loadEnvironmentCostServerBundle(join(directory, 'manifest.json'))
  assert.equal(runtime.nodeSourceIds.length, 3)
  assert.equal(runtime.directedPhysicalIndexes.length, 3)
  assert.equal(runtime.costsByTimestamp.size, 2)
  const forwardIndex = Array.from({ length: runtime.directedPhysicalIndexes.length }, (_, index) => index)
    .find((index) => runtime.directedEdgeId(index) === 'osm-way-101-0:forward')
  assert.notEqual(forwardIndex, undefined)
  assert.deepEqual(runtime.directedEdgeGeometry(forwardIndex), [[139.7357, 35.6902], [139.736, 35.6904]])
  assert.deepEqual(runtime.directedEdgeCost(forwardIndex, '2025-08-01T08:00:00+09:00'), {
    status: 'partial', sampleCount: 3, validSampleCount: 2, noGroundSampleCount: 1,
    shadeRatio: 0.25, solarExposureSeconds: 75,
  })

  const v2Directory = join(directory, 'v2')
  await writeServerBundle(v2Directory, v2Bundle)
  await validateBundle(join(v2Directory, 'manifest.json'))
  const v2Runtime = await loadEnvironmentCostServerBundle(join(v2Directory, 'manifest.json'))
  assert.equal(v2Runtime.nodeId(0), 'ped:1')
  const v2Forward = Array.from({ length: v2Runtime.directedPhysicalIndexes.length }, (_, index) => index).find((index) => v2Runtime.directedEdgeId(index) === 'ped:one:forward')
  assert.deepEqual(v2Runtime.directedEdgeGeometry(v2Forward), [[139.7357, 35.6902], [139.73585, 35.69035], [139.736, 35.6904]])
  const v2Service = await RouteService.load([{ manifestPath: join(v2Directory, 'manifest.json') }])
  assert.equal(v2Service.compare({ areaId: inputs.graph.areaId, scenarioId: 'policy', timestamp: '2025-08-01T08:00:00+09:00', start: [139.7357, 35.6902], end: [139.7363, 35.6906] }).routes[0].geometry.coordinates.length, 4, 'v2 route API must retain sidewalk polyline geometry')
  await assert.rejects(
    () => RouteService.load([{ manifestPath: join(directory, 'manifest.json'), scenarioId: 'baseline' }, { manifestPath: join(v2Directory, 'manifest.json'), scenarioId: 'policy' }]),
    /Scenario conditions do not match/,
    'v1/v2 bundles must not be used in one A/B comparison even if their area matches',
  )

  // v2 Runtime output can exceed Node's maximum string length.  Exercise the
  // file-oriented path with a header that crosses a read-stream chunk and
  // verify it produces the exact same bundle documents as the in-memory API.
  const streamedEnvironmentPath = join(directory, 'runtime-result.json')
  const streamedEnvironment = { ...v2Environment, provenance: { ...v2Environment.provenance, parserContractPadding: 'x'.repeat(128 * 1024) } }
  await writeFile(streamedEnvironmentPath, JSON.stringify(streamedEnvironment))
  const streamedDirectory = join(directory, 'v2-streamed')
  const streamedWrite = await writeV2ServerBundleFromRuntimeFile(streamedDirectory, v2Graph, streamedEnvironmentPath, { provenance: 'fixture' })
  assert.equal(streamedWrite.manifest.bundleFingerprintSha256, (await writeServerBundle(join(directory, 'v2-reference'), v2Bundle)).manifest.bundleFingerprintSha256, 'streamed v2 output must preserve the existing bundle schema and fingerprints')
  for (const file of ['topology.json', 'cost-08.json', 'cost-09.json']) {
    assert.deepEqual(JSON.parse(await readFile(join(streamedDirectory, file), 'utf8')), JSON.parse(await readFile(join(directory, 'v2-reference', file), 'utf8')), `streamed v2 ${file} must match the in-memory bundle`)
  }

  const v2ManifestPath = join(v2Directory, 'manifest.json')
  const v2Manifest = JSON.parse(await readFile(v2ManifestPath, 'utf8'))
  const v2TopologyPath = join(v2Directory, 'topology.json')
  const tamperedTopology = JSON.parse(await readFile(v2TopologyPath, 'utf8'))
  tamperedTopology.directedEdges[0][3] = 0
  const withoutFingerprint = { ...tamperedTopology }
  delete withoutFingerprint.contentFingerprintSha256
  tamperedTopology.contentFingerprintSha256 = createHash('sha256').update(JSON.stringify(withoutFingerprint)).digest('hex')
  const tamperedTopologyText = `${JSON.stringify(tamperedTopology)}\n`
  await writeFile(v2TopologyPath, tamperedTopologyText)
  v2Manifest.topology.bytes = Buffer.byteLength(tamperedTopologyText)
  v2Manifest.topology.fileSha256 = createHash('sha256').update(tamperedTopologyText).digest('hex')
  v2Manifest.topology.contentFingerprintSha256 = tamperedTopology.contentFingerprintSha256
  v2Manifest.bundleFingerprintSha256 = createHash('sha256').update(JSON.stringify({ inputs: v2Manifest.inputs, topology: v2Manifest.topology, costSlices: v2Manifest.costSlices })).digest('hex')
  await writeFile(v2ManifestPath, `${JSON.stringify(v2Manifest)}\n`)
  await assert.rejects(() => loadEnvironmentCostServerBundle(v2ManifestPath), /direction code disagrees/, 'loader must reject a v2 topology whose authenticated direction code was tampered')

  await writeFile(join(directory, 'v2-manifest.json'), JSON.stringify({ schemaVersion: 'environment-cost-server-bundle-2.0' }))
  await assert.rejects(
    () => loadEnvironmentCostServerBundle(join(directory, 'v2-manifest.json')),
    /not completed/,
    'an incomplete v2 bundle must not be accepted',
  )

  const costPath = join(directory, 'cost-08.json')
  const originalCost = await readFile(costPath, 'utf8')
  await writeFile(costPath, `${originalCost} `)
  await assert.rejects(
    () => loadEnvironmentCostServerBundle(join(directory, 'manifest.json')),
    /file size mismatch/,
    'tampered bundle files must be rejected before routing data is exposed',
  )
} finally {
  await rm(directory, { recursive: true, force: true })
}

console.log('ENVIRONMENT_COST_SERVER_BUNDLE_TEST_PASSED')
