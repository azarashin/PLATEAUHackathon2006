import { randomUUID } from 'node:crypto'
import { createServer } from 'node:http'
import { RouteError } from './route-error.mjs'

const DEFAULT_MAXIMUM_BODY_BYTES = 16 * 1024
const DEFAULT_REQUEST_TIMEOUT_MILLISECONDS = 10_000

function sendJson(response, status, document, corsOrigin) {
  const body = Buffer.from(`${JSON.stringify(document)}\n`)
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': body.length,
    'cache-control': 'no-store',
    'x-content-type-options': 'nosniff',
    ...(corsOrigin ? { 'access-control-allow-origin': corsOrigin, vary: 'Origin' } : {}),
  })
  response.end(body)
}

async function readJsonBody(request, maximumBodyBytes) {
  const chunks = []
  let length = 0
  for await (const chunk of request) {
    length += chunk.length
    if (length > maximumBodyBytes) throw new RouteError('REQUEST_TOO_LARGE', 'The request body is too large.', 413)
    chunks.push(chunk)
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'))
  } catch {
    throw new RouteError('INVALID_JSON', 'The request body is not valid JSON.', 400)
  }
}

export function createRouteHttpServer(routeService, options = {}) {
  const maximumBodyBytes = options.maximumBodyBytes ?? DEFAULT_MAXIMUM_BODY_BYTES
  const requestTimeoutMilliseconds = options.requestTimeoutMilliseconds ?? DEFAULT_REQUEST_TIMEOUT_MILLISECONDS
  const corsOrigin = options.corsOrigin
  return createServer(async (request, response) => {
    const requestId = randomUUID()
    request.setTimeout(requestTimeoutMilliseconds, () => request.destroy(new RouteError('REQUEST_TIMEOUT', 'The request timed out.', 408)))
    try {
      const url = new URL(request.url, 'http://route-server.local')
      if (request.method === 'GET' && url.pathname === '/healthz') {
        sendJson(response, 200, { status: 'ok' }, corsOrigin)
        return
      }
      const isRouteEndpoint = url.pathname === '/api/v1/routes'
      const isRoadEdgeEndpoint = url.pathname === '/api/v1/road-edges'
      if (request.method === 'OPTIONS' && (isRouteEndpoint || isRoadEdgeEndpoint)) {
        response.writeHead(204, {
          'access-control-allow-methods': `${isRouteEndpoint ? 'POST' : 'GET'}, OPTIONS`,
          'access-control-allow-headers': 'content-type',
          'access-control-max-age': '600',
          ...(corsOrigin ? { 'access-control-allow-origin': corsOrigin, vary: 'Origin' } : {}),
        })
        response.end()
        return
      }
      if (!isRouteEndpoint && !isRoadEdgeEndpoint) throw new RouteError('NOT_FOUND', 'The requested endpoint does not exist.', 404)
      let result
      if (isRouteEndpoint) {
        if (request.method !== 'POST') throw new RouteError('METHOD_NOT_ALLOWED', 'Use POST for route requests.', 405)
        result = routeService.compare(await readJsonBody(request, maximumBodyBytes))
      } else {
        if (request.method !== 'GET') throw new RouteError('METHOD_NOT_ALLOWED', 'Use GET for road edge requests.', 405)
        const bbox = url.searchParams.get('bbox')?.split(',').map(Number)
        const factor = url.searchParams.get('solarAvoidanceFactor')
        result = routeService.roadEdges({
          areaId: url.searchParams.get('areaId'),
          timestamp: url.searchParams.get('timestamp'),
          bbox,
          solarAvoidanceFactor: factor === null ? null : Number(factor),
        })
      }
      sendJson(response, 200, { requestId, ...result }, corsOrigin)
    } catch (error) {
      const known = error instanceof RouteError
      sendJson(response, known ? error.status : 500, {
        requestId,
        error: {
          code: known ? error.code : 'INTERNAL_ERROR',
          message: known ? error.message : 'The route server could not process the request.',
          ...(known && error.details !== undefined ? { details: error.details } : {}),
        },
      }, corsOrigin)
    }
  })
}
