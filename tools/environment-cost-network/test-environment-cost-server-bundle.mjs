import assert from 'node:assert/strict'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { buildServerBundleDocuments, writeServerBundle } from './build-environment-cost-server-bundle.mjs'
import { createFixtureInputs } from './fixture-inputs.mjs'
import { loadEnvironmentCostServerBundle, safeReferencedPath } from './load-environment-cost-server-bundle.mjs'

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
