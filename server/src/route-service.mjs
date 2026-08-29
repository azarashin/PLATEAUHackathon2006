import { loadEnvironmentCostServerBundle } from '../../tools/environment-cost-network/load-environment-cost-server-bundle.mjs'
import { DEFAULT_PROFILES, RouteEngine } from './route-engine.mjs'
import { RoadEdgeIndex } from './road-edge-index.mjs'
import { RouteError, invariantRoute } from './route-error.mjs'

const SAFE_ID_PATTERN = /^[a-z][a-z0-9-]{0,31}$/
const DATE_TIME_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/
const MAXIMUM_BBOX_SPAN_DEGREES = 0.2

function hasOnlyKeys(value, allowed) { return Object.keys(value).every((key) => allowed.has(key)) }

function validateScenarioId(value) {
  invariantRoute(typeof value === 'string' && SAFE_ID_PATTERN.test(value), 'INVALID_SCENARIO', 'scenarioId must be a safe identifier.', 400)
  return value
}

function validateProfiles(profiles) {
  invariantRoute(Array.isArray(profiles) && profiles.length > 0 && profiles.length <= 5, 'INVALID_PROFILE', 'profiles must contain between 1 and 5 entries.', 400)
  const ids = new Set()
  return profiles.map((profile) => {
    invariantRoute(profile && typeof profile === 'object' && SAFE_ID_PATTERN.test(profile.id ?? ''), 'INVALID_PROFILE', 'Each profile requires a safe id.', 400)
    invariantRoute(hasOnlyKeys(profile, new Set(['id', 'solarAvoidanceFactor'])), 'INVALID_PROFILE', 'A profile contains an unsupported field.', 400)
    invariantRoute(!ids.has(profile.id), 'INVALID_PROFILE', 'Profile ids must be unique.', 400)
    ids.add(profile.id)
    invariantRoute(Number.isFinite(profile.solarAvoidanceFactor) && profile.solarAvoidanceFactor >= 0 && profile.solarAvoidanceFactor <= 100, 'INVALID_PROFILE', 'solarAvoidanceFactor must be between 0 and 100.', 400)
    return { id: profile.id, solarAvoidanceFactor: profile.solarAvoidanceFactor }
  })
}

function validateRequest(request, allowedKeys = new Set(['areaId', 'timestamp', 'start', 'end', 'profiles', 'scenarioId'])) {
  invariantRoute(request && typeof request === 'object' && !Array.isArray(request), 'INVALID_REQUEST', 'The request body must be an object.', 400)
  invariantRoute(hasOnlyKeys(request, allowedKeys), 'INVALID_REQUEST', 'The request contains an unsupported field.', 400)
  invariantRoute(typeof request.areaId === 'string' && request.areaId.length > 0 && request.areaId.length <= 128, 'INVALID_REQUEST', 'areaId is required and must not exceed 128 characters.', 400)
  invariantRoute(typeof request.timestamp === 'string' && DATE_TIME_PATTERN.test(request.timestamp) && Number.isFinite(Date.parse(request.timestamp)), 'INVALID_REQUEST', 'timestamp must be an ISO 8601 date-time.', 400)
  return { areaId: request.areaId, timestamp: request.timestamp, start: request.start, end: request.end, profiles: validateProfiles(request.profiles ?? DEFAULT_PROFILES), scenarioId: request.scenarioId === undefined ? 'baseline' : validateScenarioId(request.scenarioId) }
}

function validateScenarioComparisonRequest(request) {
  const validated = validateRequest(request, new Set(['areaId', 'timestamp', 'start', 'end', 'profiles', 'baselineScenarioId', 'policyScenarioId']))
  return { ...validated, baselineScenarioId: validateScenarioId(request.baselineScenarioId ?? 'baseline'), policyScenarioId: validateScenarioId(request.policyScenarioId) }
}

function validateRoadEdgeRequest(request) {
  invariantRoute(request && typeof request === 'object' && !Array.isArray(request), 'INVALID_REQUEST', 'The road edge query must be an object.', 400)
  invariantRoute(hasOnlyKeys(request, new Set(['areaId', 'timestamp', 'bbox', 'solarAvoidanceFactor', 'scenarioId'])), 'INVALID_REQUEST', 'The road edge query contains an unsupported field.', 400)
  invariantRoute(typeof request.areaId === 'string' && request.areaId.length > 0 && request.areaId.length <= 128, 'INVALID_REQUEST', 'areaId is required and must not exceed 128 characters.', 400)
  invariantRoute(typeof request.timestamp === 'string' && DATE_TIME_PATTERN.test(request.timestamp) && Number.isFinite(Date.parse(request.timestamp)), 'INVALID_REQUEST', 'timestamp must be an ISO 8601 date-time.', 400)
  invariantRoute(Array.isArray(request.bbox) && request.bbox.length === 4 && request.bbox.every(Number.isFinite), 'INVALID_BBOX', 'bbox must be [minimumLongitude, minimumLatitude, maximumLongitude, maximumLatitude].', 400)
  const [minimumLongitude, minimumLatitude, maximumLongitude, maximumLatitude] = request.bbox
  invariantRoute(minimumLongitude >= -180 && maximumLongitude <= 180 && minimumLatitude >= -90 && maximumLatitude <= 90 && minimumLongitude < maximumLongitude && minimumLatitude < maximumLatitude, 'INVALID_BBOX', 'bbox is outside the WGS84 range or has an invalid order.', 400)
  invariantRoute(maximumLongitude - minimumLongitude <= MAXIMUM_BBOX_SPAN_DEGREES && maximumLatitude - minimumLatitude <= MAXIMUM_BBOX_SPAN_DEGREES, 'BBOX_TOO_LARGE', 'The requested map extent is too large.', 422, { maximumSpanDegrees: MAXIMUM_BBOX_SPAN_DEGREES })
  invariantRoute(Number.isFinite(request.solarAvoidanceFactor) && request.solarAvoidanceFactor >= 0 && request.solarAvoidanceFactor <= 100, 'INVALID_PROFILE', 'solarAvoidanceFactor must be between 0 and 100.', 400)
  return { areaId: request.areaId, timestamp: request.timestamp, bbox: [...request.bbox], solarAvoidanceFactor: request.solarAvoidanceFactor, scenarioId: request.scenarioId === undefined ? 'baseline' : validateScenarioId(request.scenarioId) }
}

function policyScenarioFromManifest(manifest, configuredScenarioId) {
  const metadata = manifest.policyScenario
  if (metadata === undefined) {
    invariantRoute(configuredScenarioId === undefined || configuredScenarioId === 'baseline', 'INVALID_SERVER_CONFIG', 'A legacy bundle can only be configured as baseline.', 500)
    return { id: 'baseline', label: '現状', fingerprintSha256: null, generatedAt: manifest.dataset.generatedAt }
  }
  invariantRoute(metadata && typeof metadata === 'object' && SAFE_ID_PATTERN.test(metadata.id ?? ''), 'INVALID_SERVER_CONFIG', 'Bundle policyScenario.id is invalid.', 500)
  invariantRoute(typeof metadata.label === 'string' && metadata.label.length > 0 && metadata.label.length <= 128, 'INVALID_SERVER_CONFIG', 'Bundle policyScenario.label is invalid.', 500)
  invariantRoute(typeof metadata.fingerprintSha256 === 'string' && /^[0-9a-f]{64}$/.test(metadata.fingerprintSha256), 'INVALID_SERVER_CONFIG', 'Bundle policyScenario.fingerprintSha256 is invalid.', 500)
  invariantRoute(configuredScenarioId === undefined || configuredScenarioId === metadata.id, 'INVALID_SERVER_CONFIG', 'Configured scenarioId does not match bundle policyScenario.id.', 500)
  return { id: metadata.id, label: metadata.label, fingerprintSha256: metadata.fingerprintSha256, generatedAt: manifest.dataset.generatedAt }
}

function sameComparisonConditions(left, right) {
  const leftManifest = left.runtime.manifest
  const rightManifest = right.runtime.manifest
  return leftManifest.inputs.roadGraphFingerprintSha256 === rightManifest.inputs.roadGraphFingerprintSha256
    && JSON.stringify(leftManifest.area) === JSON.stringify(rightManifest.area)
    && leftManifest.scenario.referenceDate === rightManifest.scenario.referenceDate
    && leftManifest.scenario.timezone === rightManifest.scenario.timezone
    && JSON.stringify(leftManifest.scenario.availableTimestamps) === JSON.stringify(rightManifest.scenario.availableTimestamps)
}

export class RouteService {
  constructor(areas) { this.areas = areas }

  static async load(configurations) {
    invariantRoute(Array.isArray(configurations) && configurations.length > 0, 'INVALID_SERVER_CONFIG', 'At least one route area is required.', 500)
    const areas = new Map()
    for (const configuration of configurations) {
      const runtime = await loadEnvironmentCostServerBundle(configuration.manifestPath, { timestamps: configuration.timestamps })
      const areaId = runtime.manifest.area.areaId
      const scenario = policyScenarioFromManifest(runtime.manifest, configuration.scenarioId)
      const area = areas.get(areaId) ?? { scenarios: new Map() }
      invariantRoute(!area.scenarios.has(scenario.id), 'INVALID_SERVER_CONFIG', `Duplicate route scenario: ${areaId}/${scenario.id}`, 500)
      const routeScenario = { runtime, scenario, engine: new RouteEngine(runtime, { maximumSnapDistanceMeters: configuration.maximumSnapDistanceMeters }), roadEdgeIndex: new RoadEdgeIndex(runtime, { maximumFeatures: configuration.maximumRoadEdgeFeatures }) }
      for (const existing of area.scenarios.values()) invariantRoute(sameComparisonConditions(existing, routeScenario), 'INVALID_SERVER_CONFIG', `Scenario conditions do not match: ${areaId}/${scenario.id}`, 500)
      area.scenarios.set(scenario.id, routeScenario)
      areas.set(areaId, area)
    }
    return new RouteService(areas)
  }

  resolveScenario(areaId, scenarioId) {
    const area = this.areas.get(areaId)
    if (!area) throw new RouteError('AREA_NOT_FOUND', 'The requested precomputed area is not loaded.', 404, { availableAreaIds: [...this.areas.keys()] })
    const scenario = area.scenarios.get(scenarioId)
    if (!scenario) throw new RouteError('SCENARIO_NOT_FOUND', 'The requested policy scenario is not loaded.', 404, { areaId, availableScenarioIds: [...area.scenarios.keys()] })
    return scenario
  }

  compareValidated(validated, scenarioId) {
    const area = this.resolveScenario(validated.areaId, scenarioId)
    const snapStarted = performance.now()
    const start = area.engine.snap(validated.start)
    const end = area.engine.snap(validated.end)
    const snapMilliseconds = performance.now() - snapStarted
    const routes = validated.profiles.map((profile) => area.engine.route(start.nodeIndex, end.nodeIndex, validated.timestamp, profile))
    return {
      schemaVersion: 'route-response-1.0', areaId: validated.areaId, timestamp: validated.timestamp, scenario: area.scenario,
      missingCostPolicy: 'assume-fully-sun-and-report-unknown-coverage', presentation: { locale: 'ja', kpiLabels: { unknownWalkingSeconds: '不明な歩行時間' } },
      snapped: { start, end }, routes, diagnostics: { snapMilliseconds, bundleFingerprintSha256: area.runtime.manifest.bundleFingerprintSha256 },
    }
  }

  compare(request) { const validated = validateRequest(request); return this.compareValidated(validated, validated.scenarioId) }

  compareScenarios(request) {
    const validated = validateScenarioComparisonRequest(request)
    invariantRoute(validated.baselineScenarioId !== validated.policyScenarioId, 'INVALID_SCENARIO', 'baselineScenarioId and policyScenarioId must differ.', 400)
    const baseline = this.compareValidated(validated, validated.baselineScenarioId)
    const policy = this.compareValidated(validated, validated.policyScenarioId)
    return { schemaVersion: 'scenario-route-comparison-1.0', areaId: validated.areaId, timestamp: validated.timestamp, conditions: { start: validated.start, end: validated.end, profiles: validated.profiles }, baseline, policy }
  }

  roadEdges(request) {
    const validated = validateRoadEdgeRequest(request)
    const area = this.resolveScenario(validated.areaId, validated.scenarioId)
    const result = area.roadEdgeIndex.query(validated.bbox, validated.timestamp, validated.solarAvoidanceFactor)
    return {
      schemaVersion: 'road-edge-response-1.0', type: 'FeatureCollection', areaId: validated.areaId, timestamp: validated.timestamp, scenario: area.scenario,
      bbox: validated.bbox, solarAvoidanceFactor: validated.solarAvoidanceFactor, missingCostPolicy: 'assume-fully-sun-and-report-unknown-coverage', features: result.features,
      diagnostics: { edgeCount: result.features.length, queryMilliseconds: result.queryMilliseconds, bundleFingerprintSha256: area.runtime.manifest.bundleFingerprintSha256 },
    }
  }
}
