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

const manifestPaths = (process.env.ROUTE_BUNDLE_MANIFESTS ?? '').split(',').map((value) => value.trim()).filter(Boolean)
if (manifestPaths.length === 0) throw new Error('ROUTE_BUNDLE_MANIFESTS is required')
const timestamps = (process.env.ROUTE_TIMESTAMPS ?? '').split(',').map((value) => value.trim()).filter(Boolean)
const maximumSnapDistanceMeters = positiveNumber(process.env.ROUTE_MAXIMUM_SNAP_DISTANCE_METERS, 250, 'ROUTE_MAXIMUM_SNAP_DISTANCE_METERS')
const maximumRoadEdgeFeatures = positiveNumber(process.env.ROUTE_MAXIMUM_ROAD_EDGE_FEATURES, 10_000, 'ROUTE_MAXIMUM_ROAD_EDGE_FEATURES')
if (!Number.isInteger(maximumRoadEdgeFeatures)) throw new Error('ROUTE_MAXIMUM_ROAD_EDGE_FEATURES must be an integer')
const port = positiveNumber(process.env.PORT, 3000, 'PORT')
if (!Number.isInteger(port) || port > 65535) throw new Error('PORT must be an integer between 1 and 65535')

const service = await RouteService.load(manifestPaths.map((manifestPath) => ({
  manifestPath: resolve(manifestPath),
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
