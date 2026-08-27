import assert from 'node:assert/strict'
import { mergeMeshPartitionResults } from './merge-mesh-partition-results.mjs'
import { verifyMeshPartitionResult } from './verify-mesh-partition-result.mjs'

function unit(id, samples, valid, noGround, shade) {
  return {
    schemaVersion: 'environment-cost-analysis-0.2', status: 'completed', areaId: 'test-area', center: [139, 35], radiusMeters: 4000, coordinateZoneId: 9,
    source: { plateauDatasetIds: ['dataset-a'] }, settings: { hours: [10] }, meshPartition: { unitId: id },
    edges: [{ id: 'osm-way-1-0', coordinates: [[139, 35], [139.01, 35.01]], walkingSeconds: 100, sampleCount: samples, validSampleCount: valid, noGroundSampleCount: noGround,
      hourly: [{ hour: 10, timestamp: '2025-08-01T10:00:00+09:00', sunElevationDegrees: 45, shadeSampleCount: shade }] }],
  }
}

const plan = {
  schemaVersion: 'environment-cost-mesh-partition-plan-0.1', areaId: 'test-area',
  units: [
    { id: 'mesh-1', outputPath: 'one.json', datasets: [{ id: 'dataset-a' }] },
    { id: 'mesh-2', outputPath: 'two.json', datasets: [{ id: 'dataset-b' }] },
  ],
}
const output = mergeMeshPartitionResults(plan, [unit('mesh-1', 2, 2, 0, 1), unit('mesh-2', 3, 2, 1, 2)])
assert.equal(output.edges.length, 1)
assert.equal(output.edges[0].sampleCount, 5)
assert.equal(output.edges[0].validSampleCount, 4)
assert.equal(output.edges[0].hourly[0].status, 'partial')
assert.equal(output.edges[0].hourly[0].shadeRatio, 0.75)
assert.equal(output.edges[0].hourly[0].solarExposureSeconds, 25)
assert.deepEqual(output.source.plateauDatasetIds, ['dataset-a', 'dataset-b'])
assert.match(output.resultFingerprintSha256, /^[0-9a-f]{64}$/)
assert.deepEqual(verifyMeshPartitionResult(output, structuredClone(output)), { edgeCount: 1, comparedSlices: 1 })
console.log('MESH_PARTITION_MERGE_TEST_PASSED')
