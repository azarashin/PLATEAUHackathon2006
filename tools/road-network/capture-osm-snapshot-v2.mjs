#!/usr/bin/env node

import { createHash } from 'node:crypto'
import { execFile } from 'node:child_process'
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises'
import { promisify } from 'node:util'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const R = 6371008.8
const execFileAsync = promisify(execFile)
export function bboxForCircle([lon, lat], radius) {
  const d = 180 / Math.PI
  return [lat - radius / R * d, lon - radius / (R * Math.cos(lat / d)) * d, lat + radius / R * d, lon + radius / (R * Math.cos(lat / d)) * d]
}
export function queryForConfig(config) {
  const [s, w, n, e] = bboxForCircle(config.center, config.radiusMeters).map((v) => v.toFixed(6))
  return `[out:json][timeout:180];\nway["highway"](${s},${w},${n},${e})->.ways;\n(.ways;node(w.ways);relation(bw.ways););\nout body geom;\n`
}
export function parseArgs(args) {
  const options = { endpoint: 'https://overpass-api.de/api/interpreter' }
  for (let i = 0; i < args.length; i += 1) {
    if (!args[i].startsWith('--')) throw new Error('expected --option')
    if (args[i] === '--existing-snapshot') { options.existingSnapshot = true; continue }
    if (!args[i + 1] || args[i + 1].startsWith('--')) throw new Error(`missing ${args[i]}`)
    options[args[i].slice(2)] = args[++i]
  }
  for (const required of ['config', 'output', 'query', 'manifest']) if (!options[required]) throw new Error(`--${required} is required`)
  return options
}
export function validateContract(snapshot) {
  if (!Array.isArray(snapshot?.elements)) throw new Error('OSM snapshot does not contain elements')
  const types = new Set(snapshot.elements.map((e) => e?.type))
  if (snapshot.captureContractVersion !== '0.2' || !types.has('way') || !types.has('node')) throw new Error('capture-contract-0.2 requires way and node elements plus the v0.2 relation query marker; way-only snapshots are rejected')
  if (!snapshot.elements.some((e) => e.type === 'way' && Array.isArray(e.nodes) && Array.isArray(e.geometry))) throw new Error('OSM snapshot does not contain ways with node IDs and geometry')
}
async function requestOverpass(endpoint, query) {
  try {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/x-www-form-urlencoded;charset=UTF-8' },
      body: new URLSearchParams({ data: query })
    })
    if (!response.ok) throw new Error(`HTTP ${response.status}`)
    return await response.text()
  } catch (fetchError) {
    // Some Windows environments can reach Overpass through the system proxy with curl but
    // not through Node's fetch implementation.  Keep the same POST contract and use curl
    // only as a transport fallback; validation below still rejects malformed responses.
    try {
      const { stdout } = await execFileAsync('curl.exe', [
        '--fail', '--silent', '--show-error', '--max-time', '240',
        '--data-urlencode', `data=${query}`, endpoint
      ], { maxBuffer: 128 * 1024 * 1024 })
      return stdout
    } catch (curlError) {
      throw new Error(`Overpass request failed via fetch (${fetchError.message}) and curl (${curlError.message})`)
    }
  }
}
async function main() {
  const o = parseArgs(process.argv.slice(2)); const config = JSON.parse(await readFile(resolve(o.config), 'utf8'))
  if (!Array.isArray(config.center) || !Number.isFinite(config.radiusMeters) || typeof config.areaId !== 'string') throw new Error('analysis config must contain areaId, center, radiusMeters')
  const paths = ['output', 'query', 'manifest'].map((k) => resolve(o[k])); await Promise.all(paths.map((p) => mkdir(dirname(p), { recursive: true })))
  const query = queryForConfig(config); await writeFile(resolve(o.query), query)
  let snapshot; let requestedAt = null
  if (o.existingSnapshot) snapshot = JSON.parse(await readFile(resolve(o.output), 'utf8'))
  else { requestedAt = new Date().toISOString(); snapshot = JSON.parse(await requestOverpass(o.endpoint, query)); snapshot.captureContractVersion = '0.2'; await writeFile(resolve(o.output), `${JSON.stringify(snapshot)}\n`) }
  validateContract(snapshot)
  const out = resolve(o.output), info = await stat(out), bytes = await readFile(out)
  const counts = Object.fromEntries(['way', 'node', 'relation'].map((type) => [type, snapshot.elements.filter((e) => e.type === type).length]))
  const manifest = { schemaVersion: 'osm-snapshot-manifest-0.2', captureContractVersion: '0.2', areaId: config.areaId, localPath: o.output, queryPath: o.query, endpoint: o.endpoint, requestedAt, osmTimestamp: snapshot.osm3s?.timestamp_osm_base ?? null, sizeBytes: info.size, sha256: createHash('sha256').update(bytes).digest('hex'), elementCounts: counts, requiredElementTypes: ['way', 'node', 'relation'], requiredWayFields: ['id', 'nodes', 'geometry', 'tags'] }
  await writeFile(resolve(o.manifest), `${JSON.stringify(manifest, null, 2)}\n`)
  console.log(`OSM_SNAPSHOT_V2_CAPTURED area=${config.areaId} ways=${counts.way} nodes=${counts.node} relations=${counts.relation}`)
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main().catch((e) => { console.error(e.message); process.exitCode = 1 })
