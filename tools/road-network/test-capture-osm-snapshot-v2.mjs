import assert from 'node:assert/strict'
import { bboxForCircle, parseArgs, queryForConfig, validateContract } from './capture-osm-snapshot-v2.mjs'

assert.equal(bboxForCircle([139.7, 35.6], 1000).length, 4)
assert.match(queryForConfig({ center: [139.7, 35.6], radiusMeters: 1000 }), /node\(w\.ways\);relation\(bw\.ways\)/)
assert.equal(parseArgs(['--config', 'a', '--output', 'b', '--query', 'c', '--manifest', 'd', '--existing-snapshot']).existingSnapshot, true)
const valid = { captureContractVersion: '0.2', elements: [{ type: 'way', id: 1, nodes: [1, 2], geometry: [{ lon: 1, lat: 1 }, { lon: 2, lat: 2 }] }, { type: 'node', id: 1 }, { type: 'relation', id: 1 }] }
validateContract(valid)
assert.throws(() => validateContract({ elements: valid.elements.filter((e) => e.type === 'way') }), /way-only snapshots are rejected/)
console.log('OSM_SNAPSHOT_V2_CAPTURE_TEST_PASSED')
