#!/usr/bin/env node

import { createHash } from 'node:crypto'
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const EARTH_RADIUS_METERS = 6_371_008.8

function usage() {
  return `Usage: node tools/road-network/capture-osm-snapshot.mjs \\
  --config <analysis-config.json> --output <snapshot.json> --query <query.overpassql> --manifest <manifest.json> \\
  [--endpoint <overpass-interpreter-url>] [--existing-snapshot]`
}

function parseArgs(args) {
  const options = { endpoint: 'https://overpass-api.de/api/interpreter' }
  for (let index = 0; index < args.length; index += 1) {
    const name = args[index]
    if (!name?.startsWith('--')) throw new Error(usage())
    if (name === '--existing-snapshot') {
      options.existingSnapshot = true
      continue
    }
    const value = args[index + 1]
    if (value === undefined || value.startsWith('--')) throw new Error(usage())
    options[name.slice(2)] = value
    index += 1
  }
  for (const name of ['config', 'output', 'query', 'manifest']) {
    if (!options[name]) throw new Error(`--${name} is required\n${usage()}`)
  }
  return options
}

function bboxForCircle([longitude, latitude], radiusMeters) {
  if (!Number.isFinite(longitude) || !Number.isFinite(latitude) || !Number.isFinite(radiusMeters) || radiusMeters <= 0) {
    throw new Error('config center and radiusMeters must be valid')
  }
  const radians = Math.PI / 180
  const latitudeDelta = radiusMeters / EARTH_RADIUS_METERS / radians
  const longitudeDelta = radiusMeters / (EARTH_RADIUS_METERS * Math.cos(latitude * radians)) / radians
  return [latitude - latitudeDelta, longitude - longitudeDelta, latitude + latitudeDelta, longitude + longitudeDelta]
}

function queryForConfig(config) {
  const [south, west, north, east] = bboxForCircle(config.center, config.radiusMeters)
  const format = (value) => value.toFixed(6)
  return `[out:json][timeout:180];\nway["highway"](${format(south)},${format(west)},${format(north)},${format(east)});\nout body geom;\n`
}

async function main() {
  const options = parseArgs(process.argv.slice(2))
  const config = JSON.parse(await readFile(resolve(options.config), 'utf8'))
  if (typeof config.areaId !== 'string' || !Array.isArray(config.center) || !Number.isFinite(config.radiusMeters)) {
    throw new Error('analysis config must contain areaId, center, and radiusMeters')
  }
  const query = queryForConfig(config)
  const outputPath = resolve(options.output)
  const queryPath = resolve(options.query)
  await mkdir(dirname(outputPath), { recursive: true })
  await mkdir(dirname(queryPath), { recursive: true })
  await mkdir(dirname(resolve(options.manifest)), { recursive: true })
  await writeFile(queryPath, query, 'utf8')
  let snapshot
  let requestStartedAt = null
  if (options.existingSnapshot) {
    snapshot = JSON.parse(await readFile(outputPath, 'utf8'))
  } else {
    requestStartedAt = new Date().toISOString()
    const response = await fetch(options.endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded;charset=UTF-8' },
      body: new URLSearchParams({ data: query }),
    })
    if (!response.ok) throw new Error(`Overpass request failed: ${response.status} ${response.statusText}`)
    const text = await response.text()
    try {
      snapshot = JSON.parse(text)
    } catch {
      throw new Error('Overpass response was not JSON')
    }
    await writeFile(outputPath, `${JSON.stringify(snapshot)}\n`, 'utf8')
  }
  if (!Array.isArray(snapshot.elements) || !snapshot.elements.some((element) => element?.type === 'way' && Array.isArray(element.nodes) && Array.isArray(element.geometry))) {
    throw new Error('OSM snapshot does not contain ways with node IDs and geometry')
  }
  const outputStat = await stat(outputPath)
  const sha256 = createHash('sha256').update(await readFile(outputPath)).digest('hex')
  const manifest = {
    schemaVersion: 'osm-snapshot-manifest-0.1',
    areaId: config.areaId,
    localPath: options.output.replaceAll('\\', '/'),
    queryPath: options.query.replaceAll('\\', '/'),
    endpoint: options.endpoint,
    ...(requestStartedAt ? { requestedAt: requestStartedAt } : {}),
    osmTimestamp: snapshot.osm3s?.timestamp_osm_base ?? null,
    downloadedAt: new Date().toISOString(),
    sizeBytes: outputStat.size,
    sha256,
    requiredWayFields: ['id', 'nodes', 'geometry', 'tags'],
    note: 'Keep this exact local snapshot for road graph generation and downstream Unity analysis. Requerying Overpass produces a different snapshot.',
  }
  await writeFile(resolve(options.manifest), `${JSON.stringify(manifest, null, 2)}\n`, 'utf8')
  console.log(`OSM_SNAPSHOT_CAPTURED area=${config.areaId} ways=${snapshot.elements.filter((element) => element?.type === 'way').length} bytes=${outputStat.size} sha256=${sha256}`)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message)
    process.exitCode = 1
  })
}

export { bboxForCircle, parseArgs, queryForConfig }
