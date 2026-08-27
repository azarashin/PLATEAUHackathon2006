#!/usr/bin/env node
import { createHash } from 'node:crypto'
import { readFile, rename, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { pathToFileURL } from 'node:url'

function usage() {
  return 'Usage: node tools/hourly-environment-cost/merge-mesh-partition-results.mjs --plan <plan.json> --output <environment-cost.json> [--root <repository-root>]'
}

function argument(name) {
  const index = process.argv.indexOf(name)
  return index < 0 ? null : process.argv[index + 1]
}

function invariant(condition, message) {
  if (!condition) throw new Error(message)
}

function statusFor(sampleCount, validSampleCount, noGroundSampleCount, sunElevationDegrees) {
  invariant(validSampleCount + noGroundSampleCount === sampleCount, 'sample coverage counts are inconsistent')
  if (sunElevationDegrees <= 0) return { status: 'missing', exclusionReason: 'sun-below-horizon' }
  if (validSampleCount === 0) return { status: 'missing', exclusionReason: 'road-surface-not-found' }
  if (noGroundSampleCount > 0) return { status: 'partial', exclusionReason: 'some-road-samples-not-found' }
  return { status: 'available', exclusionReason: null }
}

function resultFingerprint(document) {
  const stable = {
    schemaVersion: document.schemaVersion,
    areaId: document.areaId,
    center: document.center,
    radiusMeters: document.radiusMeters,
    coordinateZoneId: document.coordinateZoneId,
    settings: document.settings,
    edges: document.edges,
  }
  return createHash('sha256').update(JSON.stringify(stable)).digest('hex')
}

export function mergeMeshPartitionResults(plan, unitDocuments) {
  invariant(plan?.schemaVersion === 'environment-cost-mesh-partition-plan-0.1', 'invalid mesh partition plan')
  invariant(Array.isArray(plan.units) && plan.units.length > 0, 'plan has no mesh units')
  invariant(unitDocuments.length === plan.units.length, 'unit result count does not match plan')
  const first = unitDocuments[0]
  const byId = new Map()
  for (const unit of plan.units) {
    const document = unitDocuments.find(value => value.meshPartition?.unitId === unit.id)
    invariant(document, `missing completed result for ${unit.id}`)
    invariant(document.schemaVersion === 'environment-cost-analysis-0.2' && document.status === 'completed', `invalid unit result for ${unit.id}`)
    invariant(document.areaId === plan.areaId, `areaId mismatch for ${unit.id}`)
    for (const edge of document.edges ?? []) {
      let combined = byId.get(edge.id)
      if (!combined) {
        combined = { edge: { ...edge, hourly: [] }, contributions: [] }
        byId.set(edge.id, combined)
      } else {
        invariant(JSON.stringify(combined.edge.coordinates) === JSON.stringify(edge.coordinates) &&
          combined.edge.walkingSeconds === edge.walkingSeconds, `edge geometry mismatch: ${edge.id}`)
      }
      combined.contributions.push(edge)
    }
  }
  const edges = [...byId.values()].map(({ edge, contributions }) => {
    edge.sampleCount = contributions.reduce((sum, value) => sum + value.sampleCount, 0)
    edge.validSampleCount = contributions.reduce((sum, value) => sum + value.validSampleCount, 0)
    edge.noGroundSampleCount = contributions.reduce((sum, value) => sum + value.noGroundSampleCount, 0)
    edge.hourly = first.settings.hours.map(hour => {
      const parts = contributions.map(edge => edge.hourly.find(value => value.hour === hour))
      invariant(parts.every(Boolean), `missing hourly contribution: ${edge.id} ${hour}`)
      const sunElevationDegrees = parts[0].sunElevationDegrees
      invariant(parts.every(value => Math.abs(value.sunElevationDegrees - sunElevationDegrees) <= 1e-9), `sun mismatch: ${edge.id} ${hour}`)
      const { status, exclusionReason } = statusFor(edge.sampleCount, edge.validSampleCount, edge.noGroundSampleCount, sunElevationDegrees)
      if (status === 'missing') return { hour, timestamp: parts[0].timestamp, status, exclusionReason, sunElevationDegrees, shadeRatio: null, solarExposureSeconds: null }
      invariant(parts.every(value => Number.isInteger(value.shadeSampleCount)), `raw shade sample count is missing: ${edge.id} ${hour}`)
      const shadeSampleCount = parts.reduce((sum, value) => sum + value.shadeSampleCount, 0)
      const shadeRatio = shadeSampleCount / edge.validSampleCount
      return { hour, timestamp: parts[0].timestamp, status, exclusionReason, sunElevationDegrees, shadeRatio,
        solarExposureSeconds: edge.walkingSeconds * (1 - shadeRatio) }
    })
    return edge
  }).sort((a, b) => a.id.localeCompare(b.id))
  const sourceIds = [...new Set(plan.units.flatMap(unit => unit.datasets.flatMap(dataset => [dataset.id])))].sort()
  const output = {
    schemaVersion: 'environment-cost-analysis-0.2', status: 'completed', analysisKey: `mesh-partition:${plan.areaId}`,
    resultFingerprintSha256: '', areaId: plan.areaId, generatedAt: new Date().toISOString(), center: first.center,
    radiusMeters: first.radiusMeters, coordinateZoneId: first.coordinateZoneId,
    source: { ...first.source, plateauDatasetIds: sourceIds }, settings: first.settings, edges,
  }
  output.resultFingerprintSha256 = resultFingerprint(output)
  return output
}

async function main() {
  const planPath = argument('--plan')
  const outputPath = argument('--output')
  invariant(planPath && outputPath, usage())
  const root = path.resolve(argument('--root') ?? process.cwd())
  const plan = JSON.parse(await readFile(path.resolve(root, planPath), 'utf8'))
  const documents = await Promise.all(plan.units.map(async unit =>
    JSON.parse(await readFile(path.resolve(root, unit.outputPath), 'utf8'))))
  const output = mergeMeshPartitionResults(plan, documents)
  const target = path.resolve(root, outputPath)
  const temporary = `${target}.partial`
  await writeFile(temporary, `${JSON.stringify(output)}\n`, 'utf8')
  await rename(temporary, target)
  console.log(`ENVIRONMENT_COST_MESH_MERGE_COMPLETE area=${output.areaId} units=${plan.units.length} edges=${output.edges.length} output=${target}`)
}

if (import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main().catch(error => { console.error(error.stack || error.message); process.exitCode = 1 })
}
