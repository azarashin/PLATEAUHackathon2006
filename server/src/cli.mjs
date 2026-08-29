#!/usr/bin/env node

import { resolve } from 'node:path'
import { createRouteHttpServer } from './http-server.mjs'
import { RouteService } from './route-service.mjs'

function positiveNumber(value, fallback, name) {
  if (value === undefined || value === '') return fallback
  const parsed = Number(value)
  if (!Number.isFinite(parsed) || parsed <= 0) throw new Error(`${name} must be a positive number`)
  return parsed
}

function scenarioBundles() {
  const raw = process.env.ROUTE_SCENARIO_BUNDLES
  if (raw) {
    const bundles = JSON.parse(raw)
    if (!Array.isArray(bundles) || bundles.length === 0 || !bundles.every((bundle) => bundle && typeof bundle === 'object' && typeof bundle.manifestPath === 'string' && (bundle.scenarioId === undefined || typeof bundle.scenarioId === 'string'))) {
      throw new Error('ROUTE_SCENARIO_BUNDLES must be a non-empty JSON array of { manifestPath, scenarioId? }')
    }
    return bundles
  }
  const manifestPaths = (process.env.ROUTE_BUNDLE_MANIFESTS ?? '').split(',').map((value) => value.trim()).filter(Boolean)
  if (manifestPaths.length === 0) throw new Error('ROUTE_BUNDLE_MANIFESTS or ROUTE_SCENARIO_BUNDLES is required')
  return manifestPaths.map((manifestPath) => ({ manifestPath }))
}

const bundles = scenarioBundles()
const timestamps = (process.env.ROUTE_TIMESTAMPS ?? '').split(',').map((value) => value.trim()).filter(Boolean)
const maximumSnapDistanceMeters = positiveNumber(process.env.ROUTE_MAXIMUM_SNAP_DISTANCE_METERS, 250, 'ROUTE_MAXIMUM_SNAP_DISTANCE_METERS')
const maximumRoadEdgeFeatures = positiveNumber(process.env.ROUTE_MAXIMUM_ROAD_EDGE_FEATURES, 10_000, 'ROUTE_MAXIMUM_ROAD_EDGE_FEATURES')
if (!Number.isInteger(maximumRoadEdgeFeatures)) throw new Error('ROUTE_MAXIMUM_ROAD_EDGE_FEATURES must be an integer')
const port = positiveNumber(process.env.PORT, 3000, 'PORT')
if (!Number.isInteger(port) || port > 65535) throw new Error('PORT must be an integer between 1 and 65535')

const service = await RouteService.load(bundles.map((bundle) => ({
  manifestPath: resolve(bundle.manifestPath),
  ...(bundle.scenarioId === undefined ? {} : { scenarioId: bundle.scenarioId }),
  ...(timestamps.length > 0 ? { timestamps } : {}),
  maximumSnapDistanceMeters,
  maximumRoadEdgeFeatures,
})))
const server = createRouteHttpServer(service, {
  corsOrigin: process.env.ROUTE_CORS_ORIGIN || undefined,
  maximumBodyBytes: positiveNumber(process.env.ROUTE_MAXIMUM_BODY_BYTES, 16 * 1024, 'ROUTE_MAXIMUM_BODY_BYTES'),
  requestTimeoutMilliseconds: positiveNumber(process.env.ROUTE_REQUEST_TIMEOUT_MILLISECONDS, 10_000, 'ROUTE_REQUEST_TIMEOUT_MILLISECONDS'),
})
server.listen(port, process.env.HOST ?? '127.0.0.1', () => {
  const address = server.address()
  console.log(`ROUTE_SERVER_READY host=${address.address} port=${address.port} areas=${[...service.areas.keys()].join(',')}`)
})
