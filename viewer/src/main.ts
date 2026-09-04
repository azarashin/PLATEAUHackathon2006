import {
  AttributionControl,
  type ExpressionSpecification,
  type GeoJSONSource,
  LngLatBounds,
  Map as MapLibreMap,
  Marker,
  NavigationControl,
  setWorkerUrl,
  type StyleSpecification,
} from 'maplibre-gl'
import 'maplibre-gl/dist/maplibre-gl.css'
import './style.css'
import { demoAreas, findCoveredArea, geolocationErrorMessage, shouldDisplayDataset, type Coordinate, type DemoArea } from './location-domain.ts'
import {
  comparisonSummary,
  DEFAULT_SHADE_FACTOR,
  formatDistance,
  formatDuration,
  formatShadeRatio,
  identicalRouteGroups,
  parseRouteResponse,
  profilesForShadeFactor,
  routesInDisplayOrder,
  type CalculatedRoute,
  type RouteProfileId,
  type RouteResponse,
} from './route-domain.ts'
import {
  parseRoadEdgeResponse,
  physicalEdgeId,
  type RoadEdgeFeature,
  type RoadEdgeResponse,
} from './road-edge-domain.ts'
import { ICHIGAYA_DEMO_ROUTE } from './demo-route.ts'
import { routeErrorMessage } from './route-error-domain.ts'

type ValueDirection = 'higher-is-better' | 'higher-is-worse'

interface ColorStop {
  value: number
  color: string
  label: string
}

interface CostMode {
  id: string
  displayName: string
  description: string
  unit: string
  range: { min: number; max: number }
  valueDirection: ValueDirection
  valueDirectionLabel: string
  displayScale: number
  routeAggregation: 'sum' | 'maximum' | 'walking-time-weighted-mean'
  colors: ColorStop[]
  sampleKpi: { label: string; value: number; unit: string }
}

interface RoadFeature {
  type: 'Feature'
  properties: {
    id: string
    name: string
    walkingSeconds: number
    costs: Record<string, number | null>
  }
  geometry: {
    type: 'LineString'
    coordinates: [number, number][]
  }
}

interface EnvironmentCostsFixture {
  type: 'FeatureCollection'
  areaId: string
  fixture: { isDummy: boolean; label: string; notice: string }
  name: string
  bbox: [number, number, number, number]
  selectedTimestamp: string
  costModes: CostMode[]
  features: RoadFeature[]
}

type EndpointKind = 'start' | 'end'
type MapAction = EndpointKind | 'inspect'
type DataState = 'available' | 'not-precomputed' | 'outside-coverage' | 'load-error'

interface AccuracyPolygon {
  type: 'Feature'
  properties: Record<string, never>
  geometry: { type: 'Polygon'; coordinates: Coordinate[][] }
}

const routeApiUrl = import.meta.env.VITE_ROUTE_API_URL ?? `${import.meta.env.BASE_URL}api/v1/routes`
const roadEdgeApiUrl = import.meta.env.VITE_ROAD_EDGE_API_URL
  ?? routeApiUrl.replace(/\/routes$/, '/road-edges')
setWorkerUrl(import.meta.env.DEV
  ? '/node_modules/maplibre-gl/dist/maplibre-gl-worker.mjs'
  : `${import.meta.env.BASE_URL}assets/maplibre-gl-worker.mjs`)

const fixtureUrl = `${import.meta.env.BASE_URL}environment-cost-road-network-v1.json`
const viewerStartedAt = performance.now()
const viewerMetrics: { fixtureLoadedMilliseconds?: number; mapStyleReadyMilliseconds?: number } = {}
const baseStyle: StyleSpecification = {
  version: 8,
  sources: {
    osm: {
      type: 'raster',
      tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
      tileSize: 256,
      maxzoom: 19,
      attribution: '© OpenStreetMap contributors',
    },
  },
  layers: [
    {
      id: 'background',
      type: 'background',
      paint: { 'background-color': '#dce7e1' },
    },
    {
      id: 'osm-basemap',
      type: 'raster',
      source: 'osm',
      paint: { 'raster-opacity': 0.82, 'raster-saturation': -0.55 },
    },
  ],
}

const appElement = document.querySelector<HTMLDivElement>('#app')
if (!appElement) throw new Error('Application root #app was not found.')
const app: HTMLDivElement = appElement

let fixture: EnvironmentCostsFixture | null = null
let selectedModeId = ''
let map: MapLibreMap | null = null
let mapStyleReady = false
let basemapWarningShown = false
let selectedArea = demoAreas.at(-1) as DemoArea
let selectedMapAction: MapAction = 'start'
let startCoordinate: Coordinate | null = null
let endCoordinate: Coordinate | null = null
let startMarker: Marker | null = null
let endMarker: Marker | null = null
let locationMarker: Marker | null = null
let routeRequestSequence = 0
let routeResponse: RouteResponse | null = null
let selectedRouteProfile: RouteProfileId = 'balanced'
let shadeFactor = DEFAULT_SHADE_FACTOR
let roadEdgeRequestSequence = 0
let roadEdgeResponse: RoadEdgeResponse | null = null
let selectedRoadEdgeId: string | null = null
const visibleRouteProfiles = new Set<RouteProfileId>(['shortest', 'balanced', 'shade'])

const routePresentations: Record<RouteProfileId, { label: string; color: string; description: string }> = {
  shortest: { label: '最短経路', color: '#d9485f', description: '歩行時間を最小化' },
  balanced: { label: 'バランス', color: '#7048c8', description: '距離と日向回避を両立' },
  shade: { label: '日陰優先', color: '#2474d2', description: '日向時間を強く回避' },
}
const selectedRouteCasingColor = '#c4b5fd'

function recordViewerMetric(name: string, milliseconds: number): void {
  const rounded = Math.max(0, milliseconds)
  console.info(`VIEWER_PERFORMANCE ${name}=${rounded.toFixed(1)}ms`)
}

function recordInitialRenderMetric(): void {
  if (viewerMetrics.fixtureLoadedMilliseconds === undefined || viewerMetrics.mapStyleReadyMilliseconds === undefined) return
  recordViewerMetric('initial-render', Math.max(viewerMetrics.fixtureLoadedMilliseconds, viewerMetrics.mapStyleReadyMilliseconds))
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"]/g, (character) => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
  })[character] ?? character)
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

function parseFixture(value: unknown): EnvironmentCostsFixture {
  if (!isRecord(value) || value.schemaVersion !== 'environment-cost-road-network-1.0') {
    throw new Error('正式データ契約 v1 ではありません。')
  }
  if (!isRecord(value.dataset) || typeof value.dataset.name !== 'string' || typeof value.dataset.notice !== 'string') {
    throw new Error('データセット情報が不正です。')
  }
  if (value.dataset.provenance !== 'fixture' && value.dataset.provenance !== 'analysis') {
    throw new Error('データ由来が不正です。')
  }
  if (!isRecord(value.area) || typeof value.area.areaId !== 'string' || !Array.isArray(value.area.bbox) || value.area.bbox.length !== 4 || !value.area.bbox.every(isFiniteNumber)) {
    throw new Error('表示範囲 bbox が不正です。')
  }
  const [minLng, minLat, maxLng, maxLat] = value.area.bbox
  if (minLng >= maxLng || minLat >= maxLat) throw new Error('表示範囲 bbox の大小関係が不正です。')
  if (!isRecord(value.scenario) || typeof value.scenario.defaultTimestamp !== 'string' || !Array.isArray(value.scenario.availableTimestamps)) {
    throw new Error('シナリオの時刻情報が不正です。')
  }
  if (!value.scenario.availableTimestamps.includes(value.scenario.defaultTimestamp)) throw new Error('既定時刻が利用可能時刻にありません。')
  const defaultTimestamp = value.scenario.defaultTimestamp
  if (!Array.isArray(value.costDefinitions)) throw new Error('コスト定義がありません。')
  if (!Array.isArray(value.edges)) throw new Error('道路データがありません。')

  const modes = value.costDefinitions.filter((candidate) => isRecord(candidate) && isRecord(candidate.presentation) && candidate.presentation.viewerMode === true).map((candidate, index): CostMode => {
    if (!isRecord(candidate)) throw new Error(`コストモード ${index + 1} が不正です。`)
    const range = candidate.range
    const presentation = candidate.presentation
    if (!isRecord(presentation)) throw new Error(`コストモード ${index + 1} の表示情報が不正です。`)
    const colors = presentation.colors
    if (
      typeof candidate.id !== 'string' ||
      typeof candidate.displayName !== 'string' ||
      typeof candidate.description !== 'string' ||
      typeof candidate.unit !== 'string' ||
      (candidate.valueDirection !== 'higher-is-better' && candidate.valueDirection !== 'higher-is-worse') ||
      (candidate.routeAggregation !== 'sum' && candidate.routeAggregation !== 'maximum' && candidate.routeAggregation !== 'walking-time-weighted-mean') ||
      !isRecord(range) || !isFiniteNumber(range.min) || !isFiniteNumber(range.max) || range.min >= range.max ||
      typeof presentation.displayUnit !== 'string' || !isFiniteNumber(presentation.displayScale) || presentation.displayScale <= 0 ||
      typeof presentation.valueDirectionLabel !== 'string' || typeof presentation.sampleKpiLabel !== 'string' ||
      !Array.isArray(colors) || colors.length < 2
    ) {
      throw new Error(`コストモード ${index + 1} の表示情報が不正です。`)
    }
    const parsedColors = colors.map((color, colorIndex): ColorStop => {
      if (!isRecord(color) || !isFiniteNumber(color.value) || typeof color.color !== 'string' || typeof color.label !== 'string') {
        throw new Error(`コストモード ${candidate.id} の色 ${colorIndex + 1} が不正です。`)
      }
      return { value: color.value, color: color.color, label: color.label }
    }).sort((left, right) => left.value - right.value)

    return {
      id: candidate.id,
      displayName: candidate.displayName,
      description: candidate.description,
      unit: presentation.displayUnit,
      range: { min: range.min, max: range.max },
      valueDirection: candidate.valueDirection,
      valueDirectionLabel: presentation.valueDirectionLabel,
      displayScale: presentation.displayScale,
      routeAggregation: candidate.routeAggregation,
      colors: parsedColors,
      sampleKpi: { label: presentation.sampleKpiLabel, value: 0, unit: presentation.displayUnit },
    }
  })
  if (modes.length === 0) throw new Error('Viewer表示対象のコストモードがありません。')

  const modeIds = new Set(modes.map((mode) => mode.id))
  if (modeIds.size !== modes.length) throw new Error('コストモード ID が重複しています。')

  const features = value.edges.map((candidate, index): RoadFeature => {
    if (!isRecord(candidate) || !isRecord(candidate.geometry) || !Array.isArray(candidate.timeSlices)) {
      throw new Error(`道路 ${index + 1} が不正です。`)
    }
    const geometry = candidate.geometry
    if (typeof candidate.id !== 'string' || !isFiniteNumber(candidate.walkingSeconds)) {
      throw new Error(`道路 ${index + 1} の属性が不正です。`)
    }
    if (geometry.type !== 'LineString' || !Array.isArray(geometry.coordinates) || geometry.coordinates.length < 2) {
      throw new Error(`道路 ${candidate.id} の LineString が不正です。`)
    }
    const coordinates = geometry.coordinates.map((coordinate) => {
      if (!Array.isArray(coordinate) || coordinate.length < 2 || !isFiniteNumber(coordinate[0]) || !isFiniteNumber(coordinate[1])) {
        throw new Error(`道路 ${candidate.id} の座標が不正です。`)
      }
      return [coordinate[0], coordinate[1]] as [number, number]
    })
    const selectedSlice = candidate.timeSlices.find((slice) => isRecord(slice) && slice.timestamp === defaultTimestamp)
    if (!isRecord(selectedSlice) || !isRecord(selectedSlice.values)) throw new Error(`道路 ${candidate.id} に既定時刻の値がありません。`)
    const costs: Record<string, number | null> = {}
    for (const mode of modes) {
      const cost = selectedSlice.values[mode.id]
      if (cost !== null && (!isFiniteNumber(cost) || cost < mode.range.min || cost > mode.range.max)) {
        throw new Error(`道路 ${candidate.id} の ${mode.displayName} 値が範囲外です。`)
      }
      costs[mode.id] = cost === null ? null : cost
    }
    return {
      type: 'Feature',
      properties: { id: candidate.id, name: candidate.id, walkingSeconds: candidate.walkingSeconds, costs },
      geometry: { type: 'LineString', coordinates },
    }
  })

  for (const mode of modes) {
    const availableFeatures = features.filter((feature) => feature.properties.costs[mode.id] !== null)
    const values = availableFeatures.map((feature) => feature.properties.costs[mode.id] as number)
    if (values.length === 0) continue
    if (mode.routeAggregation === 'maximum') mode.sampleKpi.value = Math.max(...values)
    else if (mode.routeAggregation === 'sum') mode.sampleKpi.value = values.reduce((total, current) => total + current, 0)
    else {
      const totalWalkingSeconds = availableFeatures.reduce((total, feature) => total + feature.properties.walkingSeconds, 0)
      mode.sampleKpi.value = availableFeatures.reduce((total, feature) => total + (feature.properties.costs[mode.id] as number) * feature.properties.walkingSeconds, 0) / totalWalkingSeconds
    }
  }

  return {
    type: 'FeatureCollection',
    areaId: value.area.areaId,
    fixture: {
      isDummy: value.dataset.provenance === 'fixture',
      label: value.dataset.provenance === 'fixture' ? '正式契約ダミーデータ' : '実解析データ',
      notice: value.dataset.notice,
    },
    name: value.dataset.name,
    bbox: [minLng, minLat, maxLng, maxLat],
    selectedTimestamp: defaultTimestamp,
    costModes: modes,
    features,
  }
}

function formatValue(value: number, unit: string, displayScale = 1): string {
  const displayed = value * displayScale
  return `${Number.isInteger(displayed) ? displayed : displayed.toFixed(2)}${unit}`
}

function toRgb(hex: string): [number, number, number] {
  const value = hex.replace('#', '')
  return [0, 2, 4].map((offset) => Number.parseInt(value.slice(offset, offset + 2), 16)) as [number, number, number]
}

function colorForValue(value: number, stops: ColorStop[]): string {
  const ordered = [...stops].sort((left, right) => left.value - right.value)
  const first = ordered[0]
  const last = ordered.at(-1)
  if (!first || !last || value <= first.value) return first?.color ?? '#64748b'
  if (value >= last.value) return last.color
  const upperIndex = ordered.findIndex((stop) => stop.value >= value)
  const lower = ordered[upperIndex - 1]
  const upper = ordered[upperIndex]
  if (!lower || !upper) return first.color
  const progress = (value - lower.value) / (upper.value - lower.value)
  const lowerRgb = toRgb(lower.color)
  const upperRgb = toRgb(upper.color)
  const mixed = lowerRgb.map((channel, index) => Math.round(channel + (upperRgb[index] - channel) * progress))
  return `rgb(${mixed.join(' ')})`
}

function activeMode(): CostMode {
  const mode = fixture?.costModes.find((candidate) => candidate.id === selectedModeId)
  if (!mode) throw new Error('選択中のコストモードがありません。')
  return mode
}

function renderShell(): void {
  app.innerHTML = `
    <main class="viewer-shell">
      <header class="topbar">
        <div>
          <p class="eyebrow">Environmental Cost Route Map</p>
          <h1>環境コスト経路マップ</h1>
        </div>
        <span class="dummy-badge" id="dummy-badge">コンセプト表示</span>
      </header>

      <div class="concept-notice" id="dataset-notice" role="note">
        <strong>デモ画面</strong>
        <span>環境コストと経路の値はダミーです。避難判断や安全保証には使用できません。</span>
      </div>

      <section class="mode-panel" aria-label="環境コストモード">
        <div>
          <p class="panel-label">環境コスト</p>
          <div class="mode-buttons" id="mode-buttons" aria-live="polite"></div>
        </div>
      </section>

      <section class="location-panel" aria-label="地域と現在位置">
        <label>シミュレーション地域
          <select id="area-select">
            ${demoAreas.map((area) => `<option value="${area.id}"${area.id === selectedArea.id ? ' selected' : ''}>${escapeHtml(area.name)}（${escapeHtml(area.centerName)}）</option>`).join('')}
          </select>
        </label>
        <button id="current-location-button" class="secondary-button" type="button">現在位置へ移動</button>
        <p id="coverage-status" class="coverage-status" data-state="available" role="status">市ヶ谷周辺の計算済みデータを利用できます。</p>
        <p class="privacy-note">現在位置はこの端末での地図表示だけに使い、保存・サーバー送信しません。</p>
      </section>

      <section class="workspace">
        <div class="map-column">
          <div class="map-card">
            <div class="map-heading">
              <div>
                <p class="eyebrow">Interactive map</p>
                <h2 id="map-title">地図を準備しています</h2>
              </div>
              <span class="direction" id="direction-label">読込中</span>
            </div>
            <div class="map-wrap">
              <div id="map" aria-label="環境コスト道路地図"></div>
              <svg class="road-overlay" id="road-overlay" aria-label="環境コスト道路レイヤー"></svg>
              <span class="map-data-label" id="map-data-label">道路レイヤー準備中</span>
              <div class="map-state" id="map-state" role="status">
                <span class="spinner" aria-hidden="true"></span>
                <strong>正式契約データを読み込んでいます</strong>
                <small>しばらくお待ちください</small>
              </div>
              <div class="basemap-warning" id="basemap-warning" hidden>背景地図を取得できません。道路データは引き続き操作できます。</div>
              <div class="map-instruction">地図をクリックして<span id="click-target-label">出発地</span>を指定</div>
            </div>
          </div>

          <section class="route-preview" aria-labelledby="route-preview-title">
            <div class="section-heading">
              <div>
                <p class="eyebrow">Route comparison</p>
                <h2 id="route-preview-title">経路比較</h2>
              </div>
              <span class="preview-label" id="route-result-label">経路未計算</span>
            </div>
            <p class="route-summary" id="route-summary">起終点を指定すると、実計算した3経路を比較できます。</p>
            <div class="route-cards" id="route-cards"></div>
            <p class="identical-route-note" id="identical-route-note" hidden></p>
          </section>
        </div>

        <aside class="sidebar">
          <section class="controls-card" aria-labelledby="conditions-title">
            <div class="section-heading">
              <div>
                <p class="eyebrow">Search conditions</p>
                <h2 id="conditions-title">経路条件</h2>
              </div>
              <span class="preview-label preview-label--active">操作可能</span>
            </div>
            <div class="endpoint-controls" role="group" aria-label="地図クリックの設定先">
              <button id="select-start-button" class="endpoint-button is-active" type="button">出発地を指定</button>
              <button id="select-end-button" class="endpoint-button" type="button">目的地を指定</button>
              <button id="inspect-edge-button" class="endpoint-button" type="button">道路詳細を確認</button>
            </div>
            <label>出発地
              <span class="coordinate-field"><output id="start-coordinate">未指定</output><button id="clear-start-button" type="button">解除</button></span>
            </label>
            <label>目的地
              <span class="coordinate-field"><output id="end-coordinate">未指定</output><button id="clear-end-button" type="button">解除</button></span>
            </label>
            <div class="endpoint-actions">
              <button id="swap-endpoints-button" type="button">起終点を入れ替え</button>
              <button id="reset-conditions-button" type="button">全リセット</button>
              <button id="apply-demo-route-button" type="button">市ヶ谷デモ条件を設定</button>
            </div>
            <div class="condition-row">
              <label>計算済み日時<select id="timestamp-select"></select></label>
            </div>
            <label class="factor-control">日陰優先度
              <span><input id="shade-factor" type="range" min="0" max="4" step="0.25" value="${DEFAULT_SHADE_FACTOR}"><output id="shade-factor-value">${DEFAULT_SHADE_FACTOR.toFixed(2)}</output></span>
            </label>
            <p class="condition-help">0は距離のみ、値を上げるほど日向時間を強く回避します。バランス経路には表示値の1/4を適用します。</p>
            <p class="condition-help">選択肢は事前計算済みの日時だけです。リアルタイム解析ではありません。</p>
            <button class="search-button" id="search-button" type="button" disabled>出発地と目的地を指定してください</button>
            <p id="route-status" class="route-status" role="status">経路条件を指定してください。</p>
          </section>

          <section class="details-card" aria-live="polite">
            <p class="eyebrow">Mode detail</p>
            <h2 id="mode-title">データ読込中</h2>
            <p class="description" id="mode-description">fixture を確認しています。</p>
            <div class="kpi" id="mode-sample-kpi">
              <span id="kpi-label">サンプル KPI</span>
              <strong id="kpi-value">–</strong>
              <small>表示確認用の架空値</small>
            </div>
            <div class="legend">
              <div class="legend-title"><span>道路の凡例</span><small id="legend-range">–</small></div>
              <ul id="legend-list"></ul>
            </div>
            <p class="fixture-notice" id="fixture-notice">ダミーデータを読み込んでいます。</p>
          </section>

          <section class="road-evidence-card" aria-labelledby="road-evidence-title">
            <div class="section-heading">
              <div>
                <p class="eyebrow">Analysis evidence</p>
                <h2 id="road-evidence-title">日陰解析と探索コスト</h2>
              </div>
              <span class="preview-label" id="road-edge-count">未取得</span>
            </div>
            <p class="road-evidence-help">道路の緑・黄・橙・灰は解析値（日陰率）、明るい紫の縁を持つ線は選択中の移動経路を示します。道路を選択したときの探索コストは、歩行時間と日射回避係数から別に計算します。</p>
            <ul class="road-edge-legend" aria-label="日陰解析道路の凡例">
              <li><span style="--edge-color:#16805a"></span><strong>日陰</strong><small>日陰率75%以上</small></li>
              <li><span style="--edge-color:#e9c46a"></span><strong>混在</strong><small>日陰率25〜75%</small></li>
              <li><span style="--edge-color:#e76f51"></span><strong>日向</strong><small>日陰率25%未満</small></li>
              <li><span class="is-missing" style="--edge-color:#64748b"></span><strong>欠測</strong><small>道路面未照合／未計算</small></li>
              <li><span class="is-route"></span><strong>選択経路</strong><small>経路色＋明るい紫の縁</small></li>
            </ul>
            <p class="road-edge-status" id="road-edge-status" role="status">地図を拡大すると、表示範囲の実解析道路を取得します。</p>
            <div class="road-edge-detail is-empty" id="road-edge-detail">
              「道路詳細を確認」を選び、色付きの道路をクリックしてください。
            </div>
          </section>
        </aside>
      </section>
    </main>
  `
}

function showDataState(kind: 'empty' | 'error', title: string, detail: string): void {
  const state = document.querySelector<HTMLDivElement>('#map-state')
  if (!state) return
  state.className = `map-state map-state--${kind}`
  state.innerHTML = `<strong>${escapeHtml(title)}</strong><small>${escapeHtml(detail)}</small>`
}

function updateModeUi(): void {
  if (!fixture) return
  const mode = activeMode()
  const actualShadeMode = mode.id === 'shadeRatio' && selectedArea.availableTimestamps.length > 0
  const modeButtons = document.querySelector<HTMLDivElement>('#mode-buttons')
  const title = document.querySelector<HTMLElement>('#mode-title')
  const mapTitle = document.querySelector<HTMLElement>('#map-title')
  const description = document.querySelector<HTMLElement>('#mode-description')
  const direction = document.querySelector<HTMLElement>('#direction-label')
  const kpiLabel = document.querySelector<HTMLElement>('#kpi-label')
  const kpiValue = document.querySelector<HTMLElement>('#kpi-value')
  const legendRange = document.querySelector<HTMLElement>('#legend-range')
  const legendTitle = document.querySelector<HTMLElement>('.legend-title span')
  const legendList = document.querySelector<HTMLUListElement>('#legend-list')
  const sampleKpi = document.querySelector<HTMLElement>('#mode-sample-kpi')
  const fixtureNotice = document.querySelector<HTMLElement>('#fixture-notice')
  const badge = document.querySelector<HTMLElement>('#dummy-badge')
  const datasetNotice = document.querySelector<HTMLElement>('#dataset-notice')

  if (modeButtons) {
    modeButtons.innerHTML = fixture.costModes.map((candidate) => `
      <button class="mode-button${candidate.id === mode.id ? ' is-active' : ''}" type="button"
        data-mode-id="${escapeHtml(candidate.id)}" aria-pressed="${candidate.id === mode.id}">
        <span>${escapeHtml(candidate.displayName)}</span>
        <small>${escapeHtml(candidate.valueDirectionLabel)}</small>
      </button>
    `).join('')
    modeButtons.querySelectorAll<HTMLButtonElement>('[data-mode-id]').forEach((button) => {
      button.addEventListener('click', () => selectMode(button.dataset.modeId ?? mode.id))
    })
  }
  if (title) title.textContent = actualShadeMode ? '実日陰区間' : mode.displayName
  if (mapTitle) mapTitle.textContent = actualShadeMode ? `${selectedArea.name}の実日陰区間` : `${mode.displayName}コスト`
  if (description) description.textContent = actualShadeMode
    ? '道路の色は選択日時にUnityで解析した日陰率です。探索コストは日陰率そのものではなく、日射曝露時間と日射回避係数から計算します。'
    : mode.description
  if (direction) {
    direction.textContent = actualShadeMode ? '日陰率が高いほど緑' : mode.valueDirectionLabel
    direction.className = `direction direction--${mode.valueDirection}`
  }
  if (sampleKpi) sampleKpi.hidden = actualShadeMode
  if (fixtureNotice) fixtureNotice.hidden = actualShadeMode
  if (badge) badge.textContent = actualShadeMode ? '実日陰解析' : fixture.fixture.label
  if (datasetNotice && actualShadeMode) {
    datasetNotice.innerHTML = `<strong>${escapeHtml(selectedArea.name)} 実解析</strong><span>道路辺の日陰率と日射曝露時間はUnity解析結果、経路と探索コストは経路サーバーの計算結果です。日陰率は体感温度ではなく、安全を保証するナビではありません。</span>`
  } else if (datasetNotice) {
    datasetNotice.innerHTML = `<strong>${escapeHtml(fixture.name)}</strong><span>${escapeHtml(fixture.fixture.notice)}</span>`
  }
  if (kpiLabel) kpiLabel.textContent = mode.sampleKpi.label
  if (kpiValue) kpiValue.textContent = formatValue(mode.sampleKpi.value, mode.sampleKpi.unit, mode.displayScale)
  if (legendRange) legendRange.textContent = actualShadeMode ? '解析値 0–100%' : `${formatValue(mode.range.min, mode.unit, mode.displayScale)}–${formatValue(mode.range.max, mode.unit, mode.displayScale)}`
  if (legendTitle) legendTitle.textContent = actualShadeMode ? '地図の凡例' : '道路の凡例'
  if (legendList) {
    legendList.innerHTML = actualShadeMode ? `
      <li><span class="legend-swatch" style="--swatch:#16805a"></span><span>日陰</span><strong>75%以上</strong></li>
      <li><span class="legend-swatch" style="--swatch:#e9c46a"></span><span>日陰・日向が混在</span><strong>25〜75%</strong></li>
      <li><span class="legend-swatch" style="--swatch:#e76f51"></span><span>日向</span><strong>25%未満</strong></li>
      <li><span class="legend-swatch legend-swatch--missing" style="--swatch:#64748b"></span><span>欠測</span><strong>未照合／未計算</strong></li>
      <li><span class="legend-swatch legend-swatch--route"></span><span>選択中の移動経路</span><strong>経路色＋明紫縁</strong></li>
    ` : mode.colors.map((stop) => `
      <li><span class="legend-swatch" style="--swatch: ${stop.color}"></span><span>${escapeHtml(stop.label)}</span><strong>${formatValue(stop.value, mode.unit, mode.displayScale)}</strong></li>
    `).join('')
  }
}

function selectMode(modeId: string): void {
  if (!fixture || !fixture.costModes.some((mode) => mode.id === modeId)) return
  selectedModeId = modeId
  if (modeId !== 'shadeRatio') invalidateRoute('内水モードの実経路計算は対象外です。日陰モードへ切り替えてください。')
  updateModeUi()
  updateEndpointUi()
  if (map) renderRoadOverlay(map, activeMode())
  if (modeId === 'shadeRatio') {
    void requestRoadEdges()
    void requestRoutes()
  } else clearRoadEdges('内水モードの実解析道路表示は対象外です。')
}

function renderRoadOverlay(mapInstance: MapLibreMap, mode: CostMode): void {
  if (!fixture) return
  const overlay = document.querySelector<SVGSVGElement>('#road-overlay')
  if (!overlay) return
  if (mode.id === 'shadeRatio' && selectedArea.availableTimestamps.length > 0) {
    overlay.innerHTML = ''
    return
  }
  if (!shouldDisplayDataset(selectedArea.id, fixture.areaId)) {
    overlay.innerHTML = ''
    return
  }
  const container = mapInstance.getContainer()
  overlay.setAttribute('viewBox', `0 0 ${container.clientWidth} ${container.clientHeight}`)
  const roadPaths = fixture.features.map((feature) => {
    const path = feature.geometry.coordinates.map(([lng, lat], index) => {
      const point = mapInstance.project([lng, lat])
      return `${index === 0 ? 'M' : 'L'} ${point.x.toFixed(2)} ${point.y.toFixed(2)}`
    }).join(' ')
    const value = feature.properties.costs[mode.id]
    const label = `${feature.properties.name}: ${value === null ? '欠測' : formatValue(value, mode.unit, mode.displayScale)}`
    const color = value === null ? '#64748b' : colorForValue(value, mode.colors)
    return `<path class="road-overlay-line" d="${path}" stroke="${color}"><title>${escapeHtml(label)}</title></path>`
  }).join('')
  const casingPaths = roadPaths.replaceAll('road-overlay-line', 'road-overlay-casing').replaceAll(/stroke="[^"]+"/g, 'stroke="#ffffff"')
  overlay.innerHTML = `<g>${casingPaths}</g><g>${roadPaths}</g>`
}

function updateRoadLayerLabel(): void {
  if (!fixture) return
  const label = document.querySelector<HTMLElement>('#map-data-label')
  if (!label) return
  if (routeResponse && roadEdgeResponse) {
    label.textContent = `${roadEdgeResponse.features.length.toLocaleString('ja-JP')}辺の日陰解析・${routeResponse.routes.length}本の経路を表示中`
    return
  }
  if (routeResponse) {
    label.textContent = `${routeResponse.routes.length}本の実計算経路を表示中（拡大すると道路別の日陰を表示）`
    return
  }
  if (roadEdgeResponse) {
    label.textContent = `${roadEdgeResponse.features.length.toLocaleString('ja-JP')}辺の実日陰解析を表示中`
    return
  }
  if (shouldDisplayDataset(selectedArea.id, fixture.areaId)) {
    label.textContent = `${fixture.features.length}本の道路データを表示中`
    return
  }
  label.textContent = selectedArea.availableTimestamps.length > 0
    ? '起終点を指定すると実道路の経路を表示します'
    : 'この地域の計算結果は未生成です（#36）'
}

function formatCoordinate(coordinate: Coordinate | null): string {
  return coordinate ? `${coordinate[1].toFixed(6)}, ${coordinate[0].toFixed(6)}` : '未指定'
}

function setCoverageState(state: DataState, message: string): void {
  const status = document.querySelector<HTMLElement>('#coverage-status')
  if (!status) return
  status.dataset.state = state
  status.textContent = message
}

function emptyRoadEdgeGeoJson() {
  return { type: 'FeatureCollection' as const, features: [] }
}

function routeProfileMembership(): Map<string, RouteProfileId[]> {
  const membership = new Map<string, RouteProfileId[]>()
  for (const route of routeResponse?.routes ?? []) {
    for (const directedEdgeId of route.edgeIds) {
      const edgeId = physicalEdgeId(directedEdgeId)
      const profiles = membership.get(edgeId) ?? []
      if (!profiles.includes(route.profile.id)) profiles.push(route.profile.id)
      membership.set(edgeId, profiles)
    }
  }
  return membership
}

function roadEdgeGeoJson() {
  if (!roadEdgeResponse) return emptyRoadEdgeGeoJson()
  const membership = routeProfileMembership()
  return {
    type: 'FeatureCollection' as const,
    features: roadEdgeResponse.features.map((feature) => {
      const routeProfiles = membership.get(feature.properties.edgeId) ?? []
      return {
        ...feature,
        properties: {
          ...feature.properties,
          selectedEdge: feature.properties.edgeId === selectedRoadEdgeId,
          routeProfiles: routeProfiles.join(','),
          selectedRoute: routeProfiles.includes(selectedRouteProfile),
        },
      }
    }),
  }
}

function ensureRoadEdgeLayers(): void {
  if (!map || !mapStyleReady || map.getSource('analyzed-road-edges')) return
  map.addSource('analyzed-road-edges', { type: 'geojson', data: emptyRoadEdgeGeoJson() })
  map.addLayer({
    id: 'analyzed-road-edges-casing',
    type: 'line',
    source: 'analyzed-road-edges',
    paint: { 'line-color': '#ffffff', 'line-width': 7, 'line-opacity': 0.82 },
    layout: { 'line-cap': 'round', 'line-join': 'round' },
  })
  map.addLayer({
    id: 'analyzed-road-edges-lines',
    type: 'line',
    source: 'analyzed-road-edges',
    filter: ['!=', ['get', 'status'], 'missing'],
    paint: {
      'line-color': ['step', ['get', 'shadeRatio'], '#e76f51', 0.25, '#e9c46a', 0.75, '#16805a'],
      'line-width': 4,
      'line-opacity': 0.9,
    },
    layout: { 'line-cap': 'round', 'line-join': 'round' },
  })
  map.addLayer({
    id: 'analyzed-road-edges-missing',
    type: 'line',
    source: 'analyzed-road-edges',
    filter: ['==', ['get', 'status'], 'missing'],
    paint: { 'line-color': '#64748b', 'line-width': 4, 'line-opacity': 0.9, 'line-dasharray': [1.3, 1.3] },
    layout: { 'line-cap': 'butt', 'line-join': 'round' },
  })
  map.addLayer({
    id: 'analyzed-road-edges-selected-route',
    type: 'line',
    source: 'analyzed-road-edges',
    filter: ['==', ['get', 'selectedRoute'], true],
    paint: { 'line-color': routePresentations[selectedRouteProfile].color, 'line-width': 6, 'line-opacity': 0.96 },
    layout: { 'line-cap': 'round', 'line-join': 'round' },
  })
  map.addLayer({
    id: 'analyzed-road-edge-selected',
    type: 'line',
    source: 'analyzed-road-edges',
    filter: ['==', ['get', 'selectedEdge'], true],
    paint: { 'line-color': '#1677ff', 'line-width': 10, 'line-opacity': 0.9 },
    layout: { 'line-cap': 'round', 'line-join': 'round' },
  })
}

function updateRoadEdgeMap(): void {
  if (!map || !mapStyleReady) return
  ensureRoadEdgeLayers()
  const source = map.getSource('analyzed-road-edges') as GeoJSONSource | undefined
  source?.setData(roadEdgeGeoJson())
  if (map.getLayer('analyzed-road-edges-selected-route')) {
    map.setPaintProperty('analyzed-road-edges-selected-route', 'line-color', routePresentations[selectedRouteProfile].color)
  }
  updateRoadLayerLabel()
}

function roadEdgeStatusLabel(status: RoadEdgeFeature['properties']['status']): string {
  return status === 'available' ? '解析値あり' : status === 'partial' ? '一部欠測' : '欠測'
}

function renderRoadEdgeDetail(): void {
  const detail = document.querySelector<HTMLElement>('#road-edge-detail')
  if (!detail) return
  const feature = roadEdgeResponse?.features.find((candidate) => candidate.properties.edgeId === selectedRoadEdgeId)
  if (!feature || !roadEdgeResponse) {
    detail.className = 'road-edge-detail is-empty'
    detail.textContent = '「道路詳細を確認」を選び、色付きの道路をクリックしてください。'
    return
  }
  const properties = feature.properties
  const routeProfiles = routeProfileMembership().get(properties.edgeId) ?? []
  const profileLabels = routeProfiles.length > 0 ? routeProfiles.map((id) => routePresentations[id].label).join('・') : '表示中の経路には含まれません'
  const analysisExposure = properties.solarExposureSeconds === null ? '欠測' : formatDuration(properties.solarExposureSeconds)
  const shade = properties.shadeRatio === null ? '欠測' : formatShadeRatio(properties.shadeRatio)
  const missingExplanation = properties.missingCostAssumptionApplied
    ? `<p class="edge-assumption"><strong>欠測時の扱い:</strong> 日陰とはみなさず、歩行時間と同じ${formatDuration(properties.assumedSolarExposureSeconds)}を全日向として探索コストに使用します。${properties.missingReason ? ` ${escapeHtml(properties.missingReason)}` : ''}</p>`
    : properties.status === 'partial'
      ? `<p class="edge-assumption"><strong>一部欠測:</strong> ${escapeHtml(properties.missingReason ?? '取得できた解析値から日射曝露時間を算出しています。')}</p>`
      : ''
  detail.className = 'road-edge-detail'
  detail.innerHTML = `
    <div class="edge-detail-heading"><strong>${escapeHtml(properties.edgeId)}</strong><span data-status="${properties.status}">${roadEdgeStatusLabel(properties.status)}</span></div>
    <p class="edge-timestamp">対象時刻 ${escapeHtml(new Date(roadEdgeResponse.timestamp).toLocaleString('ja-JP'))}</p>
    <div class="edge-value-groups">
      <section><h3>解析値</h3><dl>
        <div><dt>日陰率</dt><dd>${shade}</dd></div>
        <div><dt>日射曝露時間</dt><dd>${analysisExposure}</dd></div>
        <div><dt>解析点</dt><dd>${properties.validSampleCount}/${properties.sampleCount}点</dd></div>
      </dl></section>
      <section><h3>探索用コスト</h3><dl>
        <div><dt>歩行時間</dt><dd>${formatDuration(properties.walkingSeconds)}</dd></div>
        <div><dt>日射回避係数</dt><dd>${properties.solarAvoidanceFactor.toFixed(2)}</dd></div>
        <div><dt>環境コスト加算</dt><dd>+${formatDuration(properties.environmentalCostSeconds)}</dd></div>
        <div><dt>最終探索コスト</dt><dd>${formatDuration(properties.routeCostSeconds)}</dd></div>
      </dl></section>
    </div>
    <p class="edge-formula">${formatDuration(properties.walkingSeconds)} + ${properties.solarAvoidanceFactor.toFixed(2)} × ${formatDuration(properties.assumedSolarExposureSeconds)} = ${formatDuration(properties.routeCostSeconds)}</p>
    ${missingExplanation}
    <p class="edge-route-membership"><strong>経路との対応:</strong> ${escapeHtml(profileLabels)}</p>
  `
}

function selectRoadEdge(edgeId: string | null): void {
  selectedRoadEdgeId = edgeId
  updateRoadEdgeMap()
  renderRoadEdgeDetail()
}

function clearRoadEdges(message = '地図を拡大すると、表示範囲の実解析道路を取得します。'): void {
  roadEdgeRequestSequence += 1
  roadEdgeResponse = null
  selectedRoadEdgeId = null
  const source = map?.getSource('analyzed-road-edges') as GeoJSONSource | undefined
  source?.setData(emptyRoadEdgeGeoJson())
  const status = document.querySelector<HTMLElement>('#road-edge-status')
  const count = document.querySelector<HTMLElement>('#road-edge-count')
  if (status) status.textContent = message
  if (count) count.textContent = '未取得'
  renderRoadEdgeDetail()
  updateRoadLayerLabel()
}

async function requestRoadEdges(): Promise<void> {
  if (!map || !mapStyleReady || selectedModeId !== 'shadeRatio' || selectedArea.availableTimestamps.length === 0) {
    clearRoadEdges(selectedArea.availableTimestamps.length === 0 ? 'この地域の解析結果はまだ生成されていません。' : '日陰モードで実解析道路を表示します。')
    return
  }
  if (map.getZoom() < 14.5) {
    clearRoadEdges('道路辺ごとの実解析値を表示するには、地図をもう少し拡大してください。')
    return
  }
  const timestamp = document.querySelector<HTMLSelectElement>('#timestamp-select')?.value
  if (!timestamp) return
  const requestedAt = performance.now()
  const bounds = map.getBounds()
  const bbox: [number, number, number, number] = [bounds.getWest(), bounds.getSouth(), bounds.getEast(), bounds.getNorth()]
  const sequence = ++roadEdgeRequestSequence
  roadEdgeResponse = null
  selectedRoadEdgeId = null
  updateRoadEdgeMap()
  renderRoadEdgeDetail()
  const status = document.querySelector<HTMLElement>('#road-edge-status')
  const count = document.querySelector<HTMLElement>('#road-edge-count')
  if (status) status.textContent = '表示範囲の実解析道路を取得しています。'
  if (count) count.textContent = '取得中'
  try {
    const url = new URL(roadEdgeApiUrl, window.location.href)
    url.searchParams.set('areaId', selectedArea.id)
    url.searchParams.set('timestamp', timestamp)
    url.searchParams.set('bbox', bbox.join(','))
    url.searchParams.set('solarAvoidanceFactor', String(shadeFactor))
    let response: Response
    try {
      response = await fetch(url)
    } catch {
      throw new Error('道路辺データを取得できませんでした。時間をおいて再度お試しください。')
    }
    let document: Record<string, unknown>
    try {
      document = await response.json() as Record<string, unknown>
    } catch {
      throw new Error('道路辺サーバーの応答形式が不正です。')
    }
    if (sequence !== roadEdgeRequestSequence) return
    if (!response.ok) {
      const error = isRecord(document.error) ? document.error : {}
      const code = typeof error.code === 'string' ? error.code : 'LOAD_ERROR'
      if (code === 'TOO_MANY_ROAD_EDGES' || code === 'BBOX_TOO_LARGE') throw new Error('表示範囲の道路が多いため、地図を拡大してください。')
      if (code === 'TIMESTAMP_NOT_AVAILABLE') throw new Error('選択日時の道路辺解析値はサーバーに読み込まれていません。')
      throw new Error('道路辺データを取得できませんでした。時間をおいて再度お試しください。')
    }
    const parsed = parseRoadEdgeResponse(document)
    if (parsed.areaId !== selectedArea.id || parsed.timestamp !== timestamp || parsed.solarAvoidanceFactor !== shadeFactor) return
    roadEdgeResponse = parsed
    updateRoadEdgeMap()
    updateModeUi()
    recordViewerMetric('road-edges-to-render', performance.now() - requestedAt)
    if (count) count.textContent = `${parsed.features.length.toLocaleString('ja-JP')}辺`
    if (status) status.textContent = parsed.features.length === 0
      ? 'この表示範囲には解析済み道路辺がありません。'
      : `${parsed.features.length.toLocaleString('ja-JP')}辺を表示しています。道路詳細モードで任意の辺を選択できます。`
  } catch (error) {
    if (sequence !== roadEdgeRequestSequence) return
    const message = error instanceof Error ? error.message : '道路辺データを取得できませんでした。'
    if (status) status.textContent = message
    if (count) count.textContent = '取得失敗'
  }
}

function routeGeoJson(response: RouteResponse) {
  return {
    type: 'FeatureCollection' as const,
    features: [
      ...response.routes.filter((route) => visibleRouteProfiles.has(route.profile.id)).map((route) => ({
      type: 'Feature' as const,
      properties: {
        profileId: route.profile.id,
        selected: route.profile.id === selectedRouteProfile,
      },
      geometry: route.geometry,
      })),
    ],
  }
}

function updateRouteMap(fitToRoutes = false): void {
  if (!map || !routeResponse || !mapStyleReady) return
  const data = routeGeoJson(routeResponse)
  const source = map.getSource('calculated-routes') as GeoJSONSource | undefined
  if (source) source.setData(data)
  else {
    map.addSource('calculated-routes', { type: 'geojson', data })
    const colorExpression: ExpressionSpecification = [
      'match', ['get', 'profileId'],
      'shortest', routePresentations.shortest.color,
      'balanced', routePresentations.balanced.color,
      'shade', routePresentations.shade.color,
      'baseline', '#f59e0b',
      '#64748b',
    ]
    map.addLayer({
      id: 'calculated-routes-casing',
      type: 'line',
      source: 'calculated-routes',
      paint: { 'line-color': selectedRouteCasingColor, 'line-width': 8, 'line-opacity': 0.72 },
      layout: { 'line-cap': 'round', 'line-join': 'round' },
    })
    map.addLayer({
      id: 'calculated-routes-lines',
      type: 'line',
      source: 'calculated-routes',
      paint: { 'line-color': colorExpression, 'line-width': 4, 'line-opacity': 0.82 },
      layout: { 'line-cap': 'round', 'line-join': 'round' },
    })
    map.addLayer({
      id: 'calculated-route-selected-casing',
      type: 'line',
      source: 'calculated-routes',
      filter: ['==', ['get', 'selected'], true],
      paint: { 'line-color': selectedRouteCasingColor, 'line-width': 10, 'line-opacity': 0.9 },
      layout: { 'line-cap': 'round', 'line-join': 'round' },
    })
    map.addLayer({
      id: 'calculated-route-selected',
      type: 'line',
      source: 'calculated-routes',
      filter: ['==', ['get', 'selected'], true],
      paint: { 'line-color': colorExpression, 'line-width': 6, 'line-opacity': 1 },
      layout: { 'line-cap': 'round', 'line-join': 'round' },
    })
  }
  if (fitToRoutes) {
    const bounds = new LngLatBounds()
    for (const route of routeResponse.routes) for (const point of route.geometry.coordinates) bounds.extend(point)
    if (!bounds.isEmpty()) map.fitBounds(bounds, { padding: 48, maxZoom: 16, duration: 500 })
  }
}

function routeCard(route: CalculatedRoute, shortest: CalculatedRoute, unknownLabel: string): string {
  const presentation = routePresentations[route.profile.id]
  const selected = route.profile.id === selectedRouteProfile
  const visible = visibleRouteProfiles.has(route.profile.id)
  const difference = Math.max(0, route.kpis.walkingSeconds - shortest.kpis.walkingSeconds)
  const coverageLabel = route.kpis.coverageStatus === 'available' ? '解析値あり' : route.kpis.coverageStatus === 'partial' ? '一部欠測' : '欠測あり'
  return `
    <article class="route-card${selected ? ' is-selected' : ''}" style="--route-color: ${presentation.color}">
      <button class="route-select" type="button" data-select-route="${route.profile.id}" aria-pressed="${selected}">
        <span class="route-dot"></span>
        <span><strong>${presentation.label}</strong><small>${presentation.description}・係数${route.profile.solarAvoidanceFactor.toFixed(2)}</small></span>
      </button>
      <dl class="route-kpis">
        <div><dt>距離</dt><dd>${formatDistance(route.kpis.distanceMeters)}</dd></div>
        <div><dt>推定所要時間</dt><dd>${formatDuration(route.kpis.walkingSeconds)}</dd></div>
        <div><dt>日陰率</dt><dd>${formatShadeRatio(route.kpis.observedShadeRatio)}</dd></div>
        <div><dt>日向時間</dt><dd>${formatDuration(route.kpis.solarExposureSeconds)}</dd></div>
        <div><dt>最短との差</dt><dd>+${formatDuration(difference)}</dd></div>
        <div><dt>${escapeHtml(unknownLabel)}</dt><dd>${formatDuration(route.kpis.unknownWalkingSeconds)}</dd></div>
      </dl>
      <div class="route-card-footer">
        <span class="coverage-chip" data-coverage="${route.kpis.coverageStatus}">${coverageLabel}</span>
        <label><input type="checkbox" data-toggle-route="${route.profile.id}"${visible ? ' checked' : ''}>地図に表示</label>
      </div>
    </article>
  `
}

function renderRouteComparison(fitToRoutes = false): void {
  const cards = document.querySelector<HTMLElement>('#route-cards')
  const summary = document.querySelector<HTMLElement>('#route-summary')
  const note = document.querySelector<HTMLElement>('#identical-route-note')
  const resultLabel = document.querySelector<HTMLElement>('#route-result-label')
  if (!routeResponse) {
    if (cards) cards.innerHTML = ''
    if (summary) summary.textContent = '起終点を指定すると、実計算した3経路を比較できます。'
    if (note) note.hidden = true
    if (resultLabel) resultLabel.textContent = '経路未計算'
    return
  }
  const routes = routesInDisplayOrder(routeResponse.routes)
  const shortest = routes.find((route) => route.profile.id === 'shortest')
  const selected = routes.find((route) => route.profile.id === selectedRouteProfile)
  if (!shortest || !selected) return
  const unknownLabel = routeResponse.presentation.kpiLabels.unknownWalkingSeconds
  if (cards) {
    cards.innerHTML = routes.map((route) => routeCard(route, shortest, unknownLabel)).join('')
    cards.querySelectorAll<HTMLButtonElement>('[data-select-route]').forEach((button) => button.addEventListener('click', () => {
      selectedRouteProfile = button.dataset.selectRoute as RouteProfileId
      visibleRouteProfiles.add(selectedRouteProfile)
      renderRouteComparison()
      updateRoadEdgeMap()
      renderRoadEdgeDetail()
    }))
    cards.querySelectorAll<HTMLInputElement>('[data-toggle-route]').forEach((checkbox) => checkbox.addEventListener('change', () => {
      const profileId = checkbox.dataset.toggleRoute as RouteProfileId
      if (checkbox.checked) visibleRouteProfiles.add(profileId)
      else visibleRouteProfiles.delete(profileId)
      updateRouteMap()
    }))
  }
  if (summary) {
    const policySummary = `${routePresentations[selected.profile.id].label}: ${comparisonSummary(selected, shortest)}`
    summary.textContent = policySummary
  }
  if (resultLabel) {
    resultLabel.textContent = '実計算結果'
    resultLabel.classList.add('preview-label--active')
  }
  const duplicateGroups = identicalRouteGroups(routes)
  if (note) {
    note.hidden = duplicateGroups.length === 0
    note.textContent = duplicateGroups.map((group) => `${group.map((id) => routePresentations[id].label).join('・')}は同一路線です。係数が異なっても、この条件では最適な道路列が一致しました。`).join(' ')
  }
  updateRouteMap(fitToRoutes)
}

function clearCalculatedRoutes(): void {
  routeResponse = null
  const source = map?.getSource('calculated-routes') as GeoJSONSource | undefined
  source?.setData({ type: 'FeatureCollection', features: [] })
  renderRouteComparison()
  updateRoadEdgeMap()
  renderRoadEdgeDetail()
}

function invalidateRoute(message = '条件が変更されました。新しい経路を計算します。'): void {
  routeRequestSequence += 1
  clearCalculatedRoutes()
  const status = document.querySelector<HTMLElement>('#route-status')
  if (status) status.textContent = message
}

function markerElement(kind: 'start' | 'end' | 'location'): HTMLDivElement {
  const element = document.createElement('div')
  element.className = `map-marker map-marker--${kind}`
  element.textContent = kind === 'start' ? '出' : kind === 'end' ? '着' : '●'
  element.setAttribute('aria-label', kind === 'start' ? '出発地' : kind === 'end' ? '目的地' : '現在位置')
  return element
}

function replaceMarker(marker: Marker | null, coordinate: Coordinate | null, kind: 'start' | 'end'): Marker | null {
  marker?.remove()
  if (!map || !coordinate) return null
  return new Marker({ element: markerElement(kind), anchor: 'bottom' }).setLngLat(coordinate).addTo(map)
}

function updateEndpointUi(): void {
  const startOutput = document.querySelector<HTMLOutputElement>('#start-coordinate')
  const endOutput = document.querySelector<HTMLOutputElement>('#end-coordinate')
  if (startOutput) startOutput.value = formatCoordinate(startCoordinate)
  if (endOutput) endOutput.value = formatCoordinate(endCoordinate)
  startMarker = replaceMarker(startMarker, startCoordinate, 'start')
  endMarker = replaceMarker(endMarker, endCoordinate, 'end')
  const canSearch = startCoordinate !== null && endCoordinate !== null && selectedArea.availableTimestamps.length > 0 && selectedModeId === 'shadeRatio'
  const searchButton = document.querySelector<HTMLButtonElement>('#search-button')
  if (searchButton) {
    searchButton.disabled = !canSearch
    searchButton.textContent = canSearch ? '3経路を再計算' : selectedArea.availableTimestamps.length === 0 ? 'この地域は結果未生成です' : selectedModeId !== 'shadeRatio' ? '日陰モードで利用できます' : '出発地と目的地を指定してください'
  }
}

function chooseMapAction(kind: MapAction): void {
  selectedMapAction = kind
  document.querySelector('#select-start-button')?.classList.toggle('is-active', kind === 'start')
  document.querySelector('#select-end-button')?.classList.toggle('is-active', kind === 'end')
  document.querySelector('#inspect-edge-button')?.classList.toggle('is-active', kind === 'inspect')
  const label = document.querySelector<HTMLElement>('#click-target-label')
  if (label) label.textContent = kind === 'start' ? '出発地' : kind === 'end' ? '目的地' : '確認する道路'
}

function setEndpoint(kind: EndpointKind, coordinate: Coordinate | null): void {
  if (kind === 'start') startCoordinate = coordinate
  else endCoordinate = coordinate
  invalidateRoute(coordinate ? `${kind === 'start' ? '出発地' : '目的地'}を指定しました。` : `${kind === 'start' ? '出発地' : '目的地'}を解除しました。`)
  updateEndpointUi()
  if (coordinate) chooseMapAction(kind === 'start' ? 'end' : 'start')
  void requestRoutes()
}

function updateTimestampOptions(): void {
  const select = document.querySelector<HTMLSelectElement>('#timestamp-select')
  if (!select) return
  select.disabled = selectedArea.availableTimestamps.length === 0
  select.innerHTML = selectedArea.availableTimestamps.length > 0
    ? selectedArea.availableTimestamps.map((timestamp) => `<option value="${escapeHtml(timestamp)}">${escapeHtml(new Date(timestamp).toLocaleString('ja-JP'))}</option>`).join('')
    : '<option value="">計算済みデータなし</option>'
}

function selectArea(areaId: string, moveMap = true): void {
  const area = demoAreas.find((candidate) => candidate.id === areaId)
  if (!area) return
  selectedArea = area
  clearRoadEdges('地域を変更したため、以前の道路辺詳細を消去しました。')
  const select = document.querySelector<HTMLSelectElement>('#area-select')
  if (select) select.value = area.id
  invalidateRoute('地域を変更したため、以前の経路とKPIを消去しました。')
  startCoordinate = null
  endCoordinate = null
  updateTimestampOptions()
  updateEndpointUi()
  chooseMapAction('start')
  if (area.availableTimestamps.length > 0) setCoverageState('available', `${area.name}の計算済みデータを利用できます。`)
  else setCoverageState('not-precomputed', `${area.name}は固定シミュレーション地域ですが、計算結果はまだ生成されていません。`)
  if (moveMap) map?.flyTo({ center: area.center, zoom: 12.5, essential: true })
  if (map && fixture) renderRoadOverlay(map, activeMode())
  updateModeUi()
  updateRoadLayerLabel()
}

function applyIchigayaDemoRoute(): void {
  const preset = ICHIGAYA_DEMO_ROUTE
  selectArea(preset.areaId)
  selectedModeId = 'shadeRatio'
  shadeFactor = preset.shadeFactor
  const factorInput = document.querySelector<HTMLInputElement>('#shade-factor')
  const factorOutput = document.querySelector<HTMLOutputElement>('#shade-factor-value')
  if (factorInput) factorInput.value = preset.shadeFactor.toFixed(2)
  if (factorOutput) factorOutput.value = preset.shadeFactor.toFixed(2)
  const timestampSelect = document.querySelector<HTMLSelectElement>('#timestamp-select')
  if (timestampSelect) timestampSelect.value = preset.timestamp
  startCoordinate = [...preset.start]
  endCoordinate = [...preset.end]
  updateModeUi()
  updateEndpointUi()
  invalidateRoute('市ヶ谷の固定デモ条件を設定しました。3経路を計算します。')
  void requestRoadEdges()
  void requestRoutes()
}

function accuracyPolygon(center: Coordinate, radiusMeters: number): AccuracyPolygon {
  const coordinates: Coordinate[] = []
  const latitudeScale = 111320
  const longitudeScale = latitudeScale * Math.cos(center[1] * Math.PI / 180)
  for (let index = 0; index <= 64; index += 1) {
    const angle = index / 64 * Math.PI * 2
    coordinates.push([center[0] + Math.cos(angle) * radiusMeters / longitudeScale, center[1] + Math.sin(angle) * radiusMeters / latitudeScale])
  }
  return { type: 'Feature', properties: {}, geometry: { type: 'Polygon', coordinates: [coordinates] } }
}

function showCurrentLocation(coordinate: Coordinate, accuracyMeters: number): void {
  if (!map) return
  locationMarker?.remove()
  locationMarker = new Marker({ element: markerElement('location') }).setLngLat(coordinate).addTo(map)
  const accuracy = accuracyPolygon(coordinate, accuracyMeters)
  const source = map.getSource('current-location-accuracy') as GeoJSONSource | undefined
  if (source) source.setData(accuracy)
  else {
    map.addSource('current-location-accuracy', { type: 'geojson', data: accuracy })
    map.addLayer({ id: 'current-location-accuracy-fill', type: 'fill', source: 'current-location-accuracy', paint: { 'fill-color': '#1677ff', 'fill-opacity': 0.13 } })
    map.addLayer({ id: 'current-location-accuracy-line', type: 'line', source: 'current-location-accuracy', paint: { 'line-color': '#1677ff', 'line-width': 2 } })
  }
  map.flyTo({ center: coordinate, zoom: 15, essential: true })
}

function requestCurrentLocation(): void {
  const button = document.querySelector<HTMLButtonElement>('#current-location-button')
  if (!window.isSecureContext) {
    setCoverageState('load-error', '現在位置はHTTPSまたはlocalhostでのみ取得できます。')
    return
  }
  if (!navigator.geolocation) {
    setCoverageState('load-error', 'このブラウザでは現在位置を取得できません。')
    return
  }
  if (button) button.disabled = true
  setCoverageState('available', '現在位置を取得しています。')
  navigator.geolocation.getCurrentPosition((position) => {
    if (button) button.disabled = false
    const coordinate: Coordinate = [position.coords.longitude, position.coords.latitude]
    showCurrentLocation(coordinate, position.coords.accuracy)
    const coveredArea = findCoveredArea(coordinate)
    if (!coveredArea) {
      setCoverageState('outside-coverage', `現在位置（精度±${Math.round(position.coords.accuracy)} m）は固定5地域の範囲外です。地図表示は継続できます。`)
      return
    }
    selectArea(coveredArea.id, false)
    map?.flyTo({ center: coordinate, zoom: 15, essential: true })
    setCoverageState(coveredArea.availableTimestamps.length > 0 ? 'available' : 'not-precomputed', `${coveredArea.name}の範囲内です（測位精度±${Math.round(position.coords.accuracy)} m）。${coveredArea.availableTimestamps.length > 0 ? '計算済み結果を利用できます。' : '計算結果はまだ生成されていません。'}`)
  }, (error) => {
    if (button) button.disabled = false
    setCoverageState('load-error', geolocationErrorMessage(error.code))
  }, { enableHighAccuracy: true, timeout: 10_000, maximumAge: 0 })
}

async function requestRoutes(): Promise<void> {
  if (!startCoordinate || !endCoordinate || selectedArea.availableTimestamps.length === 0 || selectedModeId !== 'shadeRatio') return
  const timestamp = document.querySelector<HTMLSelectElement>('#timestamp-select')?.value
  if (!timestamp) return
  const requestedAt = performance.now()
  const sequence = ++routeRequestSequence
  const status = document.querySelector<HTMLElement>('#route-status')
  if (status) status.textContent = '道路へのスナップと3経路の計算を実行しています。'
  setCoverageState('available', `${selectedArea.name}の計算済みデータを利用しています。`)
  try {
    let response: Response
    try {
      response = await fetch(routeApiUrl, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          areaId: selectedArea.id,
          timestamp,
          start: startCoordinate,
          end: endCoordinate,
          profiles: profilesForShadeFactor(shadeFactor),
          scenarioId: 'baseline',
        }),
      })
    } catch {
      throw new Error('経路データを取得できませんでした。時間をおいて再度お試しください。')
    }
    let responseDocument: Record<string, unknown>
    try {
      responseDocument = await response.json() as Record<string, unknown>
    } catch {
      throw new Error('経路サーバーの応答形式が不正です。')
    }
    if (sequence !== routeRequestSequence) return
    if (!response.ok) {
      const error = isRecord(responseDocument.error) ? responseDocument.error : {}
      const code = typeof error.code === 'string' ? error.code : 'LOAD_ERROR'
      throw new Error(routeErrorMessage(code))
    }
    routeResponse = parseRouteResponse(responseDocument)
    startCoordinate = routeResponse.snapped.start.snappedCoordinate
    endCoordinate = routeResponse.snapped.end.snappedCoordinate
    updateEndpointUi()
    renderRouteComparison(true)
    updateRoadEdgeMap()
    renderRoadEdgeDetail()
    updateRoadLayerLabel()
    recordViewerMetric('route-to-render', performance.now() - requestedAt)
    const badge = document.querySelector<HTMLElement>('#dummy-badge')
    const datasetNotice = document.querySelector<HTMLElement>('#dataset-notice')
    const sampleKpi = document.querySelector<HTMLElement>('#mode-sample-kpi')
    const fixtureNotice = document.querySelector<HTMLElement>('#fixture-notice')
    if (badge) badge.textContent = '実計算経路'
    if (datasetNotice) datasetNotice.innerHTML = '<strong>市ヶ谷 実計算</strong><span>経路とKPIは実道路グラフとUnity日陰解析結果からサーバーで計算しています。日陰率は体感温度ではなく、安全を保証するナビではありません。</span>'
    if (sampleKpi) sampleKpi.hidden = true
    if (fixtureNotice) fixtureNotice.hidden = true
    if (status) status.textContent = `起終点を道路へスナップし、${routeResponse.routes.length}経路を計算・描画しました。`
  } catch (error) {
    if (sequence !== routeRequestSequence) return
    const message = error instanceof Error ? error.message : '経路を検索できませんでした。'
    if (status) status.textContent = message
    if (message.includes('取得できません') || message.includes('応答形式が不正')) {
      setCoverageState('load-error', `${selectedArea.name}の計算結果を取得できませんでした。`)
    }
  }
}

function bindLocationControls(mapInstance: MapLibreMap): void {
  document.querySelector<HTMLSelectElement>('#area-select')?.addEventListener('change', (event) => selectArea((event.currentTarget as HTMLSelectElement).value))
  document.querySelector('#current-location-button')?.addEventListener('click', requestCurrentLocation)
  document.querySelector('#select-start-button')?.addEventListener('click', () => chooseMapAction('start'))
  document.querySelector('#select-end-button')?.addEventListener('click', () => chooseMapAction('end'))
  document.querySelector('#inspect-edge-button')?.addEventListener('click', () => chooseMapAction('inspect'))
  document.querySelector('#clear-start-button')?.addEventListener('click', () => setEndpoint('start', null))
  document.querySelector('#clear-end-button')?.addEventListener('click', () => setEndpoint('end', null))
  document.querySelector('#swap-endpoints-button')?.addEventListener('click', () => {
    ;[startCoordinate, endCoordinate] = [endCoordinate, startCoordinate]
    invalidateRoute('起終点を入れ替えました。')
    updateEndpointUi()
    void requestRoutes()
  })
  document.querySelector('#reset-conditions-button')?.addEventListener('click', () => {
    startCoordinate = null
    endCoordinate = null
    invalidateRoute('起終点と検索結果をリセットしました。')
    updateEndpointUi()
    chooseMapAction('start')
  })
  document.querySelector('#apply-demo-route-button')?.addEventListener('click', applyIchigayaDemoRoute)
  document.querySelector('#timestamp-select')?.addEventListener('change', () => {
    invalidateRoute('計算済み日時を変更しました。')
    clearRoadEdges('計算済み日時を変更したため、道路辺を更新しています。')
    void requestRoadEdges()
    void requestRoutes()
  })
  document.querySelector('#search-button')?.addEventListener('click', () => void requestRoutes())
  const factorInput = document.querySelector<HTMLInputElement>('#shade-factor')
  const factorOutput = document.querySelector<HTMLOutputElement>('#shade-factor-value')
  factorInput?.addEventListener('input', () => {
    const value = Number(factorInput.value)
    if (factorOutput) factorOutput.value = value.toFixed(2)
  })
  factorInput?.addEventListener('change', () => {
    shadeFactor = Number(factorInput.value)
    invalidateRoute('日陰優先度を変更したため、3経路を再計算します。')
    clearRoadEdges('日陰優先度を変更したため、道路辺の探索コストを更新しています。')
    void requestRoadEdges()
    void requestRoutes()
  })
  mapInstance.on('click', (event) => {
    if (selectedMapAction === 'inspect') {
      const layers = ['analyzed-road-edges-lines', 'analyzed-road-edges-missing'].filter((layerId) => mapInstance.getLayer(layerId))
      const feature = layers.length > 0 ? mapInstance.queryRenderedFeatures(event.point, { layers })[0] : undefined
      const edgeId = feature?.properties?.edgeId
      selectRoadEdge(typeof edgeId === 'string' ? edgeId : null)
      return
    }
    setEndpoint(selectedMapAction, [event.lngLat.lng, event.lngLat.lat])
  })
  mapInstance.on('mousemove', (event) => {
    if (selectedMapAction !== 'inspect') {
      mapInstance.getCanvas().style.cursor = ''
      return
    }
    const layers = ['analyzed-road-edges-lines', 'analyzed-road-edges-missing'].filter((layerId) => mapInstance.getLayer(layerId))
    mapInstance.getCanvas().style.cursor = layers.length > 0 && mapInstance.queryRenderedFeatures(event.point, { layers }).length > 0 ? 'pointer' : ''
  })
  mapInstance.on('moveend', () => void requestRoadEdges())
  updateTimestampOptions()
  updateEndpointUi()
}

function initializeMap(): void {
  if (!fixture) return
  const mode = activeMode()
  const mapInstance = new MapLibreMap({
    container: 'map',
    style: baseStyle,
    center: selectedArea.center,
    zoom: 12.5,
    attributionControl: false,
  })
  map = mapInstance
  mapStyleReady = false
  bindLocationControls(mapInstance)
  mapInstance.addControl(new NavigationControl({ showCompass: false }), 'top-right')
  mapInstance.addControl(new AttributionControl({ compact: true }), 'bottom-right')

  mapInstance.on('error', (event) => {
    const sourceId = 'sourceId' in event && typeof event.sourceId === 'string' ? event.sourceId : ''
    if (sourceId === 'osm' && !basemapWarningShown) {
      basemapWarningShown = true
      document.querySelector<HTMLElement>('#basemap-warning')?.removeAttribute('hidden')
    }
  })

  mapInstance.on('load', () => {
    if (!fixture) return
    mapStyleReady = true
    ensureRoadEdgeLayers()
    renderRoadOverlay(mapInstance, mode)
    document.querySelector<HTMLElement>('#map-state')?.setAttribute('hidden', '')
    updateRoadLayerLabel()
    updateRouteMap(routeResponse !== null)
    viewerMetrics.mapStyleReadyMilliseconds = performance.now() - viewerStartedAt
    recordViewerMetric('map-style-ready', viewerMetrics.mapStyleReadyMilliseconds)
    recordInitialRenderMetric()
    void requestRoadEdges()
  })
  mapInstance.on('render', () => renderRoadOverlay(mapInstance, activeMode()))
}

async function start(): Promise<void> {
  renderShell()
  try {
    const response = await fetch(fixtureUrl)
    if (!response.ok) throw new Error(`fixture の取得に失敗しました（HTTP ${response.status}）。`)
    fixture = parseFixture(await response.json() as unknown)
    viewerMetrics.fixtureLoadedMilliseconds = performance.now() - viewerStartedAt
    recordViewerMetric('fixture-loaded', viewerMetrics.fixtureLoadedMilliseconds)
    recordInitialRenderMetric()
    if (fixture.features.length === 0) {
      showDataState('empty', '表示できる道路がありません', 'fixture に LineString を追加して再読み込みしてください。')
      return
    }
    selectedModeId = fixture.costModes[0]?.id ?? ''
    const badge = document.querySelector<HTMLElement>('#dummy-badge')
    const notice = document.querySelector<HTMLElement>('#fixture-notice')
    const datasetNotice = document.querySelector<HTMLElement>('#dataset-notice')
    if (badge) badge.textContent = fixture.fixture.label
    if (notice) notice.textContent = fixture.fixture.notice
    if (datasetNotice) datasetNotice.innerHTML = `<strong>${escapeHtml(fixture.name)}</strong><span>${escapeHtml(fixture.fixture.notice)}</span>`
    updateModeUi()
    initializeMap()
  } catch (error) {
    const message = error instanceof Error ? error.message : '不明なエラーです。'
    showDataState('error', '道路データを読み込めませんでした', message)
    const description = document.querySelector<HTMLElement>('#mode-description')
    if (description) description.textContent = 'データを確認してからページを再読み込みしてください。'
  }
}

void start()
