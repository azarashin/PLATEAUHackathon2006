import {
  AttributionControl,
  type GeoJSONSource,
  Map as MapLibreMap,
  Marker,
  NavigationControl,
  type StyleSpecification,
} from 'maplibre-gl'
import 'maplibre-gl/dist/maplibre-gl.css'
import './style.css'
import { demoAreas, findCoveredArea, geolocationErrorMessage, shouldDisplayDataset, type Coordinate, type DemoArea } from './location-domain.ts'

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
type DataState = 'available' | 'not-precomputed' | 'outside-coverage' | 'load-error'

interface AccuracyPolygon {
  type: 'Feature'
  properties: Record<string, never>
  geometry: { type: 'Polygon'; coordinates: Coordinate[][] }
}

const routeApiUrl = import.meta.env.VITE_ROUTE_API_URL ?? `${import.meta.env.BASE_URL}api/v1/routes`

const fixtureUrl = `${import.meta.env.BASE_URL}environment-cost-road-network-v1.json`
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
let basemapWarningShown = false
let selectedArea = demoAreas.at(-1) as DemoArea
let selectedEndpoint: EndpointKind = 'start'
let startCoordinate: Coordinate | null = null
let endCoordinate: Coordinate | null = null
let startMarker: Marker | null = null
let endMarker: Marker | null = null
let locationMarker: Marker | null = null
let routeRequestSequence = 0

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
              <span class="preview-label">外観プレビュー</span>
            </div>
            <div class="route-cards">
              <article><span class="route-dot route-dot--short"></span><strong>最短経路</strong><small>実計算は未接続</small></article>
              <article><span class="route-dot route-dot--shade"></span><strong>日陰優先</strong><small>実計算は未接続</small></article>
              <article><span class="route-dot route-dot--balance"></span><strong>バランス</strong><small>実計算は未接続</small></article>
            </div>
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
            </div>
            <div class="condition-row">
              <label>計算済み日時<select id="timestamp-select"></select></label>
            </div>
            <p class="condition-help">選択肢は事前計算済みの日時だけです。リアルタイム解析ではありません。</p>
            <button class="search-button" id="search-button" type="button" disabled>出発地と目的地を指定してください</button>
            <p id="route-status" class="route-status" role="status">経路条件を指定してください。</p>
          </section>

          <section class="details-card" aria-live="polite">
            <p class="eyebrow">Mode detail</p>
            <h2 id="mode-title">データ読込中</h2>
            <p class="description" id="mode-description">fixture を確認しています。</p>
            <div class="kpi">
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
  const modeButtons = document.querySelector<HTMLDivElement>('#mode-buttons')
  const title = document.querySelector<HTMLElement>('#mode-title')
  const mapTitle = document.querySelector<HTMLElement>('#map-title')
  const description = document.querySelector<HTMLElement>('#mode-description')
  const direction = document.querySelector<HTMLElement>('#direction-label')
  const kpiLabel = document.querySelector<HTMLElement>('#kpi-label')
  const kpiValue = document.querySelector<HTMLElement>('#kpi-value')
  const legendRange = document.querySelector<HTMLElement>('#legend-range')
  const legendList = document.querySelector<HTMLUListElement>('#legend-list')

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
  if (title) title.textContent = mode.displayName
  if (mapTitle) mapTitle.textContent = `${mode.displayName}コスト`
  if (description) description.textContent = mode.description
  if (direction) {
    direction.textContent = mode.valueDirectionLabel
    direction.className = `direction direction--${mode.valueDirection}`
  }
  if (kpiLabel) kpiLabel.textContent = mode.sampleKpi.label
  if (kpiValue) kpiValue.textContent = formatValue(mode.sampleKpi.value, mode.sampleKpi.unit, mode.displayScale)
  if (legendRange) legendRange.textContent = `${formatValue(mode.range.min, mode.unit, mode.displayScale)}–${formatValue(mode.range.max, mode.unit, mode.displayScale)}`
  if (legendList) {
    legendList.innerHTML = mode.colors.map((stop) => `
      <li><span class="legend-swatch" style="--swatch: ${stop.color}"></span><span>${escapeHtml(stop.label)}</span><strong>${formatValue(stop.value, mode.unit, mode.displayScale)}</strong></li>
    `).join('')
  }
}

function selectMode(modeId: string): void {
  if (!fixture || !fixture.costModes.some((mode) => mode.id === modeId)) return
  selectedModeId = modeId
  updateModeUi()
  if (map) renderRoadOverlay(map, activeMode())
}

function renderRoadOverlay(mapInstance: MapLibreMap, mode: CostMode): void {
  if (!fixture) return
  const overlay = document.querySelector<SVGSVGElement>('#road-overlay')
  if (!overlay) return
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
  if (shouldDisplayDataset(selectedArea.id, fixture.areaId)) {
    label.textContent = `${fixture.features.length}本の道路データを表示中`
    return
  }
  label.textContent = selectedArea.availableTimestamps.length > 0
    ? '実道路・経路レイヤーは#13で接続します'
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

function invalidateRoute(message = '条件が変更されました。新しい経路を計算します。'): void {
  routeRequestSequence += 1
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
  const canSearch = startCoordinate !== null && endCoordinate !== null && selectedArea.availableTimestamps.length > 0
  const searchButton = document.querySelector<HTMLButtonElement>('#search-button')
  if (searchButton) {
    searchButton.disabled = !canSearch
    searchButton.textContent = canSearch ? '3経路を再計算' : selectedArea.availableTimestamps.length === 0 ? 'この地域は結果未生成です' : '出発地と目的地を指定してください'
  }
}

function chooseEndpoint(kind: EndpointKind): void {
  selectedEndpoint = kind
  document.querySelector('#select-start-button')?.classList.toggle('is-active', kind === 'start')
  document.querySelector('#select-end-button')?.classList.toggle('is-active', kind === 'end')
  const label = document.querySelector<HTMLElement>('#click-target-label')
  if (label) label.textContent = kind === 'start' ? '出発地' : '目的地'
}

function setEndpoint(kind: EndpointKind, coordinate: Coordinate | null): void {
  if (kind === 'start') startCoordinate = coordinate
  else endCoordinate = coordinate
  invalidateRoute(coordinate ? `${kind === 'start' ? '出発地' : '目的地'}を指定しました。` : `${kind === 'start' ? '出発地' : '目的地'}を解除しました。`)
  updateEndpointUi()
  if (coordinate) chooseEndpoint(kind === 'start' ? 'end' : 'start')
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
  const select = document.querySelector<HTMLSelectElement>('#area-select')
  if (select) select.value = area.id
  invalidateRoute('地域を変更したため、以前の経路とKPIを消去しました。')
  startCoordinate = null
  endCoordinate = null
  updateTimestampOptions()
  updateEndpointUi()
  chooseEndpoint('start')
  if (area.availableTimestamps.length > 0) setCoverageState('available', `${area.name}の計算済みデータを利用できます。`)
  else setCoverageState('not-precomputed', `${area.name}は固定シミュレーション地域ですが、計算結果はまだ生成されていません。`)
  if (moveMap) map?.flyTo({ center: area.center, zoom: 12.5, essential: true })
  if (map && fixture) renderRoadOverlay(map, activeMode())
  updateRoadLayerLabel()
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
  if (!startCoordinate || !endCoordinate || selectedArea.availableTimestamps.length === 0) return
  const timestamp = document.querySelector<HTMLSelectElement>('#timestamp-select')?.value
  if (!timestamp) return
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
        body: JSON.stringify({ areaId: selectedArea.id, timestamp, start: startCoordinate, end: endCoordinate }),
      })
    } catch {
      throw new Error('経路データを取得できませんでした。時間をおいて再度お試しください。')
    }
    let document: Record<string, unknown>
    try {
      document = await response.json() as Record<string, unknown>
    } catch {
      throw new Error('経路サーバーの応答形式が不正です。')
    }
    if (sequence !== routeRequestSequence) return
    if (!response.ok) {
      const error = isRecord(document.error) ? document.error : {}
      const code = typeof error.code === 'string' ? error.code : 'LOAD_ERROR'
      if (code === 'SNAP_NOT_FOUND') throw new Error('許容距離内に歩行可能な道路がないため、経路を検索できません。')
      if (code === 'OUTSIDE_COVERAGE') throw new Error('選択地点が計算済み範囲外のため、経路を検索できません。')
      throw new Error('経路データを取得できませんでした。時間をおいて再度お試しください。')
    }
    if (!isRecord(document.snapped) || !isRecord(document.snapped.start) || !isRecord(document.snapped.end)) throw new Error('経路サーバーの応答形式が不正です。')
    const snappedStart = document.snapped.start.snappedCoordinate
    const snappedEnd = document.snapped.end.snappedCoordinate
    if (!Array.isArray(snappedStart) || !snappedStart.every(isFiniteNumber) || !Array.isArray(snappedEnd) || !snappedEnd.every(isFiniteNumber)) throw new Error('スナップ結果が不正です。')
    startCoordinate = [snappedStart[0] as number, snappedStart[1] as number]
    endCoordinate = [snappedEnd[0] as number, snappedEnd[1] as number]
    updateEndpointUi()
    const routeCount = Array.isArray(document.routes) ? document.routes.length : 0
    if (status) status.textContent = `起終点を道路へスナップし、${routeCount}経路を計算しました。経路描画とKPI比較は#13で接続します。`
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
  document.querySelector('#select-start-button')?.addEventListener('click', () => chooseEndpoint('start'))
  document.querySelector('#select-end-button')?.addEventListener('click', () => chooseEndpoint('end'))
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
    chooseEndpoint('start')
  })
  document.querySelector('#timestamp-select')?.addEventListener('change', () => {
    invalidateRoute('計算済み日時を変更しました。')
    void requestRoutes()
  })
  document.querySelector('#search-button')?.addEventListener('click', () => void requestRoutes())
  mapInstance.on('click', (event) => setEndpoint(selectedEndpoint, [event.lngLat.lng, event.lngLat.lat]))
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
    renderRoadOverlay(mapInstance, mode)
    document.querySelector<HTMLElement>('#map-state')?.setAttribute('hidden', '')
    updateRoadLayerLabel()
  })
  mapInstance.on('render', () => renderRoadOverlay(mapInstance, activeMode()))
}

async function start(): Promise<void> {
  renderShell()
  try {
    const response = await fetch(fixtureUrl)
    if (!response.ok) throw new Error(`fixture の取得に失敗しました（HTTP ${response.status}）。`)
    fixture = parseFixture(await response.json() as unknown)
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
