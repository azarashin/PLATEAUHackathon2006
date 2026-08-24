#!/usr/bin/env node

import { createHash } from 'node:crypto'
import { readFile, mkdir, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { buildGraph, graphFingerprint, qualityReport, shortestPath } from './build-pedestrian-graph.mjs'

function usage() {
  return `Usage: node tools/road-network/verify-regional-road-network.mjs \\
  --config <analysis-config.json> --osm <snapshot.json> --snapshot-manifest <manifest.json> --overrides <overrides.geojson> \\
  --start <longitude,latitude> --end <longitude,latitude> --report <verification.json>`
}

function parseArgs(args) {
  const options = {}
  for (let index = 0; index < args.length; index += 2) {
    const name = args[index]
    const value = args[index + 1]
    if (!name?.startsWith('--') || value === undefined) throw new Error(usage())
    options[name.slice(2)] = value
  }
  for (const name of ['config', 'osm', 'snapshot-manifest', 'overrides', 'start', 'end', 'report']) {
    if (!options[name]) throw new Error(`--${name} is required\n${usage()}`)
  }
  return options
}

function coordinate(value, name) {
  const result = value.split(',').map(Number)
  if (result.length !== 2 || !result.every(Number.isFinite)) throw new Error(`${name} must be longitude,latitude`)
  return result
}

function summarizedQuality(report) {
  return {
    graphFingerprintSha256: report.graphFingerprintSha256,
    counts: report.counts,
    connectivity: report.connectivity,
    validation: report.validation,
    manualOverrides: report.manualOverrides,
  }
}

async function main() {
  const options = parseArgs(process.argv.slice(2))
  const [config, osm, snapshotManifest, overrides] = await Promise.all([
    readFile(resolve(options.config), 'utf8').then(JSON.parse),
    readFile(resolve(options.osm), 'utf8').then(JSON.parse),
    readFile(resolve(options['snapshot-manifest']), 'utf8').then(JSON.parse),
    readFile(resolve(options.overrides), 'utf8').then(JSON.parse),
  ])
  if (snapshotManifest.areaId !== config.areaId) throw new Error('snapshot manifest areaId does not match config')
  const firstGraph = buildGraph(config, osm, overrides)
  const secondGraph = buildGraph(config, osm, overrides)
  const firstFingerprint = graphFingerprint(firstGraph)
  const secondFingerprint = graphFingerprint(secondGraph)
  if (firstFingerprint !== secondFingerprint) throw new Error('same input produced different graph fingerprints')
  const input = {
    areaId: config.areaId,
    configPath: options.config,
    osmPath: options.osm,
    overridesPath: options.overrides,
    osmTimestamp: osm.osm3s?.timestamp_osm_base ?? null,
    walkingSpeedMetersPerSecond: firstGraph.walkingSpeed,
  }
  const quality = qualityReport(firstGraph, input)
  if (!quality.validation.isValid) throw new Error(`graph validation failed: ${quality.validation.failures.join(', ')}`)
  const route = shortestPath(firstGraph, coordinate(options.start, '--start'), coordinate(options.end, '--end'))
  if (!route.found) throw new Error('demo route endpoints are disconnected')
  const routeLengthBetween3And5Km = route.lengthMeters >= 3000 && route.lengthMeters <= 5000
  const endpointsWithin250Meters = route.start.distanceMeters <= 250 && route.end.distanceMeters <= 250
  if (!routeLengthBetween3And5Km) throw new Error(`demo route length is outside 3-5 km: ${route.lengthMeters}`)
  if (!endpointsWithin250Meters) throw new Error(`demo endpoint snap exceeds 250 m: ${route.start.distanceMeters}, ${route.end.distanceMeters}`)
  const document = {
    schemaVersion: 'regional-pedestrian-road-network-verification-0.1',
    verifiedAt: new Date().toISOString(),
    areaId: config.areaId,
    inputs: {
      configPath: options.config,
      osmSnapshot: snapshotManifest,
      overridesPath: options.overrides,
    },
    reproducibility: {
      firstGraphFingerprintSha256: firstFingerprint,
      secondGraphFingerprintSha256: secondFingerprint,
      matches: true,
    },
    quality: summarizedQuality(quality),
    demoRoute: {
      requestedStart: coordinate(options.start, '--start'),
      requestedEnd: coordinate(options.end, '--end'),
      snappedStart: route.start,
      snappedEnd: route.end,
      edgeCount: route.edgeIds.length,
      edgeIdFingerprintSha256: createHash('sha256').update(JSON.stringify(route.edgeIds)).digest('hex'),
      lengthMeters: route.lengthMeters,
      walkingSeconds: route.walkingSeconds,
    },
    validation: {
      graphQualityValid: true,
      deterministicFingerprint: true,
      routeFound: true,
      routeLengthBetween3And5Km,
      endpointSnapsWithin250Meters: endpointsWithin250Meters,
    },
  }
  const reportPath = resolve(options.report)
  await mkdir(dirname(reportPath), { recursive: true })
  await writeFile(reportPath, `${JSON.stringify(document, null, 2)}\n`)
  console.log(`REGIONAL_ROAD_NETWORK_VERIFIED area=${config.areaId} nodes=${quality.counts.nodeCount} directedEdges=${quality.counts.directedEdgeCount} routeMeters=${route.lengthMeters.toFixed(1)} fingerprint=${firstFingerprint}`)
}

main().catch((error) => {
  console.error(error.message)
  console.error(usage())
  process.exitCode = 1
})
