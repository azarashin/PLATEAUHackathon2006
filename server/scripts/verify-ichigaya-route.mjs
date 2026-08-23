#!/usr/bin/env node

import { createHash } from 'node:crypto'
import { mkdir, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { createRouteHttpServer } from '../src/http-server.mjs'
import { haversineMeters } from '../src/route-engine.mjs'
import { RouteService } from '../src/route-service.mjs'

const REQUEST = Object.freeze({
  areaId: 'ichigaya-venue',
  timestamp: '2025-08-01T12:00:00+09:00',
  start: Object.freeze([139.736043, 35.69047]),
  end: Object.freeze([139.700556, 35.689606]),
})

function parseArgs(args) {
  const options = { iterations: 5 }
  for (let index = 0; index < args.length; index += 2) {
    const name = args[index]
    const value = args[index + 1]
    if (!name?.startsWith('--') || value === undefined) throw new Error('Usage: verify-ichigaya-route.mjs --manifest <manifest.json> [--report <report.json>] [--iterations <positive integer>]')
    options[name.slice(2)] = value
  }
  if (!options.manifest) throw new Error('--manifest is required')
  options.iterations = Number(options.iterations)
  if (!Number.isInteger(options.iterations) || options.iterations < 2 || options.iterations > 100) throw new Error('--iterations must be an integer between 2 and 100')
  return options
}

function percentile(values, fraction) {
  const sorted = [...values].sort((left, right) => left - right)
  return sorted[Math.ceil(sorted.length * fraction) - 1]
}

function memoryDelta(after, before) {
  return Object.fromEntries(Object.keys(after).map((key) => [key, after[key] - before[key]]))
}

function fingerprintEdgeIds(edgeIds) {
  return createHash('sha256').update(JSON.stringify(edgeIds)).digest('hex')
}

async function requestOverHttp(service) {
  const server = createRouteHttpServer(service)
  await new Promise((resolveListen, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolveListen)
  })
  try {
    const port = server.address().port
    const started = performance.now()
    const response = await fetch(`http://127.0.0.1:${port}/api/v1/routes`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(REQUEST),
    })
    const bytes = Buffer.from(await response.arrayBuffer())
    const milliseconds = performance.now() - started
    if (response.status !== 200) throw new Error(`actual HTTP route failed: ${response.status} ${bytes.toString('utf8')}`)
    return { status: response.status, bytes: bytes.length, milliseconds, document: JSON.parse(bytes.toString('utf8')) }
  } finally {
    await new Promise((resolveClose, reject) => server.close((error) => error ? reject(error) : resolveClose()))
  }
}

const options = parseArgs(process.argv.slice(2))
globalThis.gc?.()
const memoryBefore = process.memoryUsage()
const loadStarted = performance.now()
const service = await RouteService.load([{
  manifestPath: resolve(options.manifest),
  timestamps: [REQUEST.timestamp],
  maximumSnapDistanceMeters: 250,
}])
const loadMilliseconds = performance.now() - loadStarted
globalThis.gc?.()
const memoryAfterLoad = process.memoryUsage()
const compareMilliseconds = []
const results = []
for (let index = 0; index < options.iterations; index += 1) {
  const started = performance.now()
  results.push(service.compare(REQUEST))
  compareMilliseconds.push(performance.now() - started)
}
const result = results[0]
const exposures = result.routes.map((route) => route.kpis.solarExposureSeconds)
if (!(exposures[0] >= exposures[1] && exposures[1] >= exposures[2])) throw new Error('solar exposure is not monotonically non-increasing across the default profiles')
if (result.routes[0].kpis.routeCostSeconds !== result.routes[0].kpis.walkingSeconds) throw new Error('factor zero does not match the minimum walking-time cost')
if (result.routes[0].kpis.distanceMeters < 3000 || result.routes[0].kpis.distanceMeters > 5000) throw new Error('the actual demo route is outside the required 3-5 km range')
for (const repeated of results.slice(1)) {
  for (let index = 0; index < result.routes.length; index += 1) {
    if (fingerprintEdgeIds(repeated.routes[index].edgeIds) !== fingerprintEdgeIds(result.routes[index].edgeIds)) throw new Error(`route edge IDs changed between runs: ${result.routes[index].profile.id}`)
  }
}
const http = await requestOverHttp(service)
if ('topology' in http.document || 'costSlices' in http.document || 'nodes' in http.document) throw new Error('the HTTP response leaked server bundle data')
const runtime = service.areas.get(REQUEST.areaId).runtime
const report = {
  schemaVersion: 'ichigaya-route-server-verification-0.1',
  verifiedAt: new Date().toISOString(),
  input: {
    manifestPath: options.manifest,
    bundleFingerprintSha256: runtime.manifest.bundleFingerprintSha256,
    counts: runtime.manifest.counts,
    request: REQUEST,
    directDistanceMeters: haversineMeters(REQUEST.start, REQUEST.end),
    iterationCount: options.iterations,
  },
  snapped: result.snapped,
  routes: result.routes.map((route) => ({
    profile: route.profile,
    edgeCount: route.edgeIds.length,
    edgeIdFingerprintSha256: fingerprintEdgeIds(route.edgeIds),
    kpis: route.kpis,
    representativeSearchMilliseconds: route.diagnostics.searchMilliseconds,
    representativeVisitedNodeCount: route.diagnostics.visitedNodeCount,
  })),
  performance: {
    loadOneTimestampMilliseconds: loadMilliseconds,
    compareMilliseconds,
    compareP50Milliseconds: percentile(compareMilliseconds, 0.5),
    compareP95Milliseconds: percentile(compareMilliseconds, 0.95),
    httpRoundTripMilliseconds: http.milliseconds,
    httpResponseBytes: http.bytes,
    memoryBefore,
    memoryAfterLoad,
    memoryDeltaAfterLoad: memoryDelta(memoryAfterLoad, memoryBefore),
  },
  validation: {
    routeDistanceBetween3And5Km: true,
    factorZeroMatchesWalkingTime: true,
    exposureMonotonicallyNonIncreasing: true,
    repeatedEdgeIdsDeterministic: true,
    httpStatusOk: true,
    browserBundleDataExcluded: true,
  },
}
if (options.report) {
  const reportPath = resolve(options.report)
  await mkdir(dirname(reportPath), { recursive: true })
  await writeFile(reportPath, `${JSON.stringify(report, null, 2)}\n`)
}
console.log(JSON.stringify(report, null, 2))
