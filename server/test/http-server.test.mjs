import assert from 'node:assert/strict'
import { after, before, test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { createRouteFixtureInputs } from '../fixtures/route-fixture-inputs.mjs'
import { createRouteHttpServer } from '../src/http-server.mjs'
import { RouteService } from '../src/route-service.mjs'

const manifestPath = fileURLToPath(new URL('../../data/fixtures/route-server-bundle-v1/manifest.json', import.meta.url))
const fixture = createRouteFixtureInputs()
let server
let baseUrl

before(async () => {
  const service = await RouteService.load([{ manifestPath, maximumSnapDistanceMeters: 100 }])
  server = createRouteHttpServer(service, { maximumBodyBytes: 4096 })
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
  baseUrl = `http://127.0.0.1:${server.address().port}`
})

after(async () => {
  await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()))
})

test('health endpoint reports readiness', async () => {
  const response = await fetch(`${baseUrl}/healthz`)
  assert.equal(response.status, 200)
  assert.deepEqual(await response.json(), { status: 'ok' })
})

test('route endpoint returns only snapped points, routes, and diagnostics', async () => {
  const response = await fetch(`${baseUrl}/api/v1/routes`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      areaId: 'route-server-fixture', timestamp: fixture.timestamp,
      start: fixture.coordinates.start, end: fixture.coordinates.end,
    }),
  })
  assert.equal(response.status, 200)
  assert.ok(Number(response.headers.get('content-length')) < 8192)
  const document = await response.json()
  assert.match(document.requestId, /^[0-9a-f-]{36}$/)
  assert.equal(document.routes.length, 3)
  assert.equal(document.routes.every((route) => route.geometry.type === 'LineString'), true)
  assert.equal('topology' in document, false)
  assert.equal('costSlices' in document, false)
})

test('invalid JSON, method, path, and oversized requests are distinguishable', async () => {
  const invalidJson = await fetch(`${baseUrl}/api/v1/routes`, { method: 'POST', body: '{' })
  assert.equal(invalidJson.status, 400)
  assert.equal((await invalidJson.json()).error.code, 'INVALID_JSON')

  const wrongMethod = await fetch(`${baseUrl}/api/v1/routes`)
  assert.equal(wrongMethod.status, 405)
  assert.equal((await wrongMethod.json()).error.code, 'METHOD_NOT_ALLOWED')

  const missing = await fetch(`${baseUrl}/missing`)
  assert.equal(missing.status, 404)
  assert.equal((await missing.json()).error.code, 'NOT_FOUND')

  const tooLarge = await fetch(`${baseUrl}/api/v1/routes`, { method: 'POST', body: JSON.stringify({ padding: 'x'.repeat(5000) }) })
  assert.equal(tooLarge.status, 413)
  assert.equal((await tooLarge.json()).error.code, 'REQUEST_TOO_LARGE')
})

test('internal failures do not disclose stack traces or file paths', async () => {
  const response = await fetch(`${baseUrl}/api/v1/routes`, {
    method: 'POST',
    body: JSON.stringify({ areaId: 'unknown', timestamp: fixture.timestamp, start: fixture.coordinates.start, end: fixture.coordinates.end }),
  })
  assert.equal(response.status, 404)
  const text = await response.text()
  assert.match(text, /AREA_NOT_FOUND/)
  assert.doesNotMatch(text, /at RouteService|[A-Z]:\\|\.mjs:/)
})
