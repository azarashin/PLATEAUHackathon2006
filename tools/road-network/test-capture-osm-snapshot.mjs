#!/usr/bin/env node

import assert from 'node:assert/strict'
import { bboxForCircle, parseArgs, queryForConfig } from './capture-osm-snapshot.mjs'

const bbox = bboxForCircle([135.75877, 34.98535], 4000)
assert.equal(bbox.length, 4)
assert.ok(bbox[0] < 34.98535 && bbox[2] > 34.98535)
assert.ok(bbox[1] < 135.75877 && bbox[3] > 135.75877)
const query = queryForConfig({ areaId: 'test', center: [135.75877, 34.98535], radiusMeters: 4000 })
assert.match(query, /^\[out:json\]\[timeout:180\];\nway\["highway"\]\(/)
assert.match(query, /\);\nout body geom;\n$/)
const parsed = parseArgs(['--config', 'config.json', '--output', 'snapshot.json', '--query', 'query.overpassql', '--manifest', 'manifest.json', '--existing-snapshot'])
assert.equal(parsed.existingSnapshot, true)
console.log('OSM_SNAPSHOT_CAPTURE_TEST_PASSED')
