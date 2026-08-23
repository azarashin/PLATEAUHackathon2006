import { loadEnvironmentCostServerBundle } from '../../tools/environment-cost-network/load-environment-cost-server-bundle.mjs'
import { DEFAULT_PROFILES, RouteEngine } from './route-engine.mjs'
import { RouteError, invariantRoute } from './route-error.mjs'

const PROFILE_ID_PATTERN = /^[a-z][a-z0-9-]{0,31}$/
const DATE_TIME_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/

function hasOnlyKeys(value, allowed) {
  return Object.keys(value).every((key) => allowed.has(key))
}

function validateProfiles(profiles) {
  invariantRoute(Array.isArray(profiles) && profiles.length > 0 && profiles.length <= 5, 'INVALID_PROFILE', 'profiles must contain between 1 and 5 entries.', 400)
  const ids = new Set()
  return profiles.map((profile) => {
    invariantRoute(profile && typeof profile === 'object' && PROFILE_ID_PATTERN.test(profile.id ?? ''), 'INVALID_PROFILE', 'Each profile requires a safe id.', 400)
    invariantRoute(hasOnlyKeys(profile, new Set(['id', 'solarAvoidanceFactor'])), 'INVALID_PROFILE', 'A profile contains an unsupported field.', 400)
    invariantRoute(!ids.has(profile.id), 'INVALID_PROFILE', 'Profile ids must be unique.', 400)
    ids.add(profile.id)
    invariantRoute(Number.isFinite(profile.solarAvoidanceFactor) && profile.solarAvoidanceFactor >= 0 && profile.solarAvoidanceFactor <= 100, 'INVALID_PROFILE', 'solarAvoidanceFactor must be between 0 and 100.', 400)
    return { id: profile.id, solarAvoidanceFactor: profile.solarAvoidanceFactor }
  })
}

function validateRequest(request) {
  invariantRoute(request && typeof request === 'object' && !Array.isArray(request), 'INVALID_REQUEST', 'The request body must be an object.', 400)
  invariantRoute(hasOnlyKeys(request, new Set(['areaId', 'timestamp', 'start', 'end', 'profiles'])), 'INVALID_REQUEST', 'The request contains an unsupported field.', 400)
  invariantRoute(typeof request.areaId === 'string' && request.areaId.length > 0 && request.areaId.length <= 128, 'INVALID_REQUEST', 'areaId is required and must not exceed 128 characters.', 400)
  invariantRoute(typeof request.timestamp === 'string' && DATE_TIME_PATTERN.test(request.timestamp) && Number.isFinite(Date.parse(request.timestamp)), 'INVALID_REQUEST', 'timestamp must be an ISO 8601 date-time.', 400)
  return {
    areaId: request.areaId,
    timestamp: request.timestamp,
    start: request.start,
    end: request.end,
    profiles: validateProfiles(request.profiles ?? DEFAULT_PROFILES),
  }
}

export class RouteService {
  constructor(areas) {
    this.areas = areas
  }

  static async load(configurations) {
    invariantRoute(Array.isArray(configurations) && configurations.length > 0, 'INVALID_SERVER_CONFIG', 'At least one route area is required.', 500)
    const areas = new Map()
    for (const configuration of configurations) {
      const runtime = await loadEnvironmentCostServerBundle(configuration.manifestPath, { timestamps: configuration.timestamps })
      const areaId = runtime.manifest.area.areaId
      invariantRoute(!areas.has(areaId), 'INVALID_SERVER_CONFIG', `Duplicate route area: ${areaId}`, 500)
      areas.set(areaId, {
        runtime,
        engine: new RouteEngine(runtime, { maximumSnapDistanceMeters: configuration.maximumSnapDistanceMeters }),
      })
    }
    return new RouteService(areas)
  }

  compare(request) {
    const validated = validateRequest(request)
    const area = this.areas.get(validated.areaId)
    if (!area) throw new RouteError('AREA_NOT_FOUND', 'The requested precomputed area is not loaded.', 404, { availableAreaIds: [...this.areas.keys()] })
    const snapStarted = performance.now()
    const start = area.engine.snap(validated.start)
    const end = area.engine.snap(validated.end)
    const snapMilliseconds = performance.now() - snapStarted
    const routes = validated.profiles.map((profile) => area.engine.route(start.nodeIndex, end.nodeIndex, validated.timestamp, profile))
    return {
      schemaVersion: 'route-response-1.0',
      areaId: validated.areaId,
      timestamp: validated.timestamp,
      missingCostPolicy: 'assume-fully-sun-and-report-unknown-coverage',
      presentation: {
        locale: 'ja',
        kpiLabels: { unknownWalkingSeconds: '不明な歩行時間' },
      },
      snapped: { start, end },
      routes,
      diagnostics: {
        snapMilliseconds,
        bundleFingerprintSha256: area.runtime.manifest.bundleFingerprintSha256,
      },
    }
  }
}
