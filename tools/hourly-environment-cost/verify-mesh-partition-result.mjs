#!/usr/bin/env node
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { pathToFileURL } from 'node:url'

const value = name => {
  const index = process.argv.indexOf(name)
  return index < 0 ? null : process.argv[index + 1]
}
const invariant = (condition, message) => { if (!condition) throw new Error(message) }

export function verifyMeshPartitionResult(monolithic, partitioned, tolerance = 1e-9) {
  invariant(monolithic?.areaId === partitioned?.areaId, 'areaId mismatch')
  const baseline = new Map((monolithic.edges ?? []).map(edge => [edge.id, edge]))
  invariant(baseline.size === (partitioned.edges ?? []).length, 'edge count mismatch')
  let comparedSlices = 0
  for (const edge of partitioned.edges) {
    const expected = baseline.get(edge.id)
    invariant(expected, `unexpected edge: ${edge.id}`)
    for (const field of ['sampleCount', 'validSampleCount', 'noGroundSampleCount']) {
      invariant(expected[field] === edge[field], `${field} mismatch: ${edge.id}`)
    }
    const expectedHourly = new Map(expected.hourly.map(slice => [slice.hour, slice]))
    for (const slice of edge.hourly) {
      const reference = expectedHourly.get(slice.hour)
      invariant(reference && reference.status === slice.status && reference.exclusionReason === slice.exclusionReason,
        `status mismatch: ${edge.id} ${slice.hour}`)
      for (const field of ['shadeRatio', 'solarExposureSeconds']) {
        if (reference[field] === null || slice[field] === null) invariant(reference[field] === slice[field], `${field} null mismatch: ${edge.id} ${slice.hour}`)
        else invariant(Math.abs(reference[field] - slice[field]) <= tolerance, `${field} mismatch: ${edge.id} ${slice.hour}`)
      }
      comparedSlices++
    }
  }
  return { edgeCount: baseline.size, comparedSlices }
}

async function main() {
  const monolithicPath = value('--monolithic')
  const partitionedPath = value('--partitioned')
  invariant(monolithicPath && partitionedPath, 'Usage: node verify-mesh-partition-result.mjs --monolithic <one-shot.json> --partitioned <merged.json> [--tolerance <number>]')
  const [monolithic, partitioned] = await Promise.all([monolithicPath, partitionedPath].map(async file => JSON.parse(await readFile(file, 'utf8'))))
  const result = verifyMeshPartitionResult(monolithic, partitioned, Number(value('--tolerance') ?? '1e-9'))
  console.log(`ENVIRONMENT_COST_MESH_COMPARE_PASSED edges=${result.edgeCount} hourly=${result.comparedSlices}`)
}

if (import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main().catch(error => { console.error(error.stack || error.message); process.exitCode = 1 })
