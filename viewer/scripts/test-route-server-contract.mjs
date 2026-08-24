import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import Ajv2020 from 'ajv/dist/2020.js'
import addFormats from 'ajv-formats'
import { createRouteFixtureInputs } from '../../server/fixtures/route-fixture-inputs.mjs'
import { RouteService } from '../../server/src/route-service.mjs'

const requestSchemaPath = fileURLToPath(new URL('../../schemas/route-request-v1.schema.json', import.meta.url))
const responseSchemaPath = fileURLToPath(new URL('../../schemas/route-response-v1.schema.json', import.meta.url))
const roadEdgeResponseSchemaPath = fileURLToPath(new URL('../../schemas/road-edge-response-v1.schema.json', import.meta.url))
const manifestPath = fileURLToPath(new URL('../../data/fixtures/route-server-bundle-v1/manifest.json', import.meta.url))
const [requestSchema, responseSchema, roadEdgeResponseSchema] = await Promise.all([
  readFile(requestSchemaPath, 'utf8').then(JSON.parse),
  readFile(responseSchemaPath, 'utf8').then(JSON.parse),
  readFile(roadEdgeResponseSchemaPath, 'utf8').then(JSON.parse),
])
const ajv = new Ajv2020({ allErrors: true, strict: true })
addFormats(ajv)
const validateRequest = ajv.compile(requestSchema)
const validateResponse = ajv.compile(responseSchema)
const validateRoadEdgeResponse = ajv.compile(roadEdgeResponseSchema)
const fixture = createRouteFixtureInputs()
const request = {
  areaId: 'route-server-fixture', timestamp: fixture.timestamp,
  start: fixture.coordinates.start, end: fixture.coordinates.end,
}
assert.equal(validateRequest(request), true, JSON.stringify(validateRequest.errors))
for (const invalid of [
  { ...request, start: [200, 35] },
  { ...request, profiles: [] },
  { ...request, profiles: [{ id: 'bad id', solarAvoidanceFactor: 1 }] },
  { ...request, extra: true },
]) {
  assert.equal(validateRequest(invalid), false, `invalid request passed: ${JSON.stringify(invalid)}`)
}
const service = await RouteService.load([{ manifestPath, maximumSnapDistanceMeters: 100 }])
const response = { requestId: randomUUID(), ...service.compare(request) }
assert.equal(validateResponse(response), true, JSON.stringify(validateResponse.errors))
const leaked = structuredClone(response)
leaked.topology = {}
assert.equal(validateResponse(leaked), false, 'a response containing the server topology must be rejected')
const roadEdges = { requestId: randomUUID(), ...service.roadEdges({
  areaId: request.areaId,
  timestamp: request.timestamp,
  bbox: [139.7349, 35.6897, 139.7361, 35.6908],
  solarAvoidanceFactor: 2,
}) }
assert.equal(validateRoadEdgeResponse(roadEdges), true, JSON.stringify(validateRoadEdgeResponse.errors))
const invalidRoadEdges = structuredClone(roadEdges)
invalidRoadEdges.features[0].properties.shadeRatio = 1.1
assert.equal(validateRoadEdgeResponse(invalidRoadEdges), false, 'an out-of-range shade ratio must be rejected')
console.log(`ROUTE_SERVER_CONTRACT_TEST_PASSED routes=${response.routes.length} roadEdges=${roadEdges.features.length}`)
