import assert from 'node:assert/strict'
import { buildEnvironmentCostRoadNetwork } from './build-environment-cost-road-network.mjs'
import { createFixtureInputs } from './fixture-inputs.mjs'

const { graph, environment } = createFixtureInputs()
assert.throws(
  () => buildEnvironmentCostRoadNetwork(graph, environment),
  /physical graph edges have no environment-cost source/,
  'strict mode must reject graph coverage gaps',
)

const result = buildEnvironmentCostRoadNetwork(graph, environment, { allowUnmatchedAsMissing: true, provenance: 'fixture' })
assert.equal(result.document.nodes.length, 3)
assert.equal(result.document.edges.length, 3)
assert.equal(result.document.scenario.availableTimestamps.length, 2)
assert.equal(result.diagnostics.matchedPhysicalEdgeCount, 1)
assert.equal(result.diagnostics.unmatchedPhysicalEdgeCount, 1)

const matched = result.document.edges.find((edge) => edge.id === 'osm-way-101-0:forward')
assert.equal(matched.timeSlices[0].status, 'partial')
assert.deepEqual(matched.timeSlices[0].sampleCoverage, { sampleCount: 3, validSampleCount: 2, noGroundSampleCount: 1 })
assert.equal(matched.timeSlices[0].values.shadeRatio, 0.25)
assert.equal(matched.timeSlices[0].values.solarExposureSeconds, 75)

const unmatched = result.document.edges.find((edge) => edge.physicalEdgeId === 'osm-way-103-0')
assert.equal(unmatched.timeSlices[0].status, 'missing')
assert.deepEqual(unmatched.timeSlices[0].values, { shadeRatio: null, solarExposureSeconds: null })

const reordered = createFixtureInputs()
reordered.graph.nodes.reverse()
reordered.graph.edges.reverse()
reordered.environment.edges.reverse()
const reorderedResult = buildEnvironmentCostRoadNetwork(reordered.graph, reordered.environment, {
  allowUnmatchedAsMissing: true,
  provenance: 'fixture',
})
assert.equal(reorderedResult.document.dataset.contentFingerprintSha256, result.document.dataset.contentFingerprintSha256)
assert.deepEqual(reorderedResult.document, result.document, 'input array ordering must not affect output')

const incomplete = createFixtureInputs()
incomplete.environment.edges[0].hourly.pop()
assert.throws(() => buildEnvironmentCostRoadNetwork(incomplete.graph, incomplete.environment, { allowUnmatchedAsMissing: true }), /hourly slice count mismatch/)

console.log('ENVIRONMENT_COST_NETWORK_TEST_PASSED')
