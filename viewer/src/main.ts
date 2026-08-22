import {
  AttributionControl,
  Map as MapLibreMap,
  NavigationControl,
  type StyleSpecification,
} from 'maplibre-gl'
import 'maplibre-gl/dist/maplibre-gl.css'
import './style.css'

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
  colors: ColorStop[]
  sampleKpi: { label: string; value: number; unit: string }
}

interface RoadFeature {
  type: 'Feature'
  properties: {
    id: string
    name: string
    costs: Record<string, number>
  }
  geometry: {
    type: 'LineString'
    coordinates: [number, number][]
  }
}

interface EnvironmentCostsFixture {
  type: 'FeatureCollection'
  fixture: { isDummy: true; label: string; notice: string }
  name: string
  bbox: [number, number, number, number]
  costModes: CostMode[]
  features: RoadFeature[]
}

const fixtureUrl = '/environment-costs-phase-a.geojson'
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
  if (!isRecord(value) || value.type !== 'FeatureCollection') {
    throw new Error('GeoJSON FeatureCollection ではありません。')
  }
  if (!isRecord(value.fixture) || value.fixture.isDummy !== true) {
    throw new Error('ダミーデータ識別情報がありません。')
  }
  if (typeof value.fixture.label !== 'string' || typeof value.fixture.notice !== 'string') {
    throw new Error('ダミーデータの表示情報が不正です。')
  }
  if (typeof value.name !== 'string') throw new Error('データ名がありません。')
  if (!Array.isArray(value.bbox) || value.bbox.length !== 4 || !value.bbox.every(isFiniteNumber)) {
    throw new Error('表示範囲 bbox が不正です。')
  }
  const [minLng, minLat, maxLng, maxLat] = value.bbox
  if (minLng >= maxLng || minLat >= maxLat) throw new Error('表示範囲 bbox の大小関係が不正です。')
  if (!Array.isArray(value.costModes) || value.costModes.length < 2) {
    throw new Error('コストモードが不足しています。')
  }
  if (!Array.isArray(value.features)) throw new Error('道路データがありません。')

  const modes = value.costModes.map((candidate, index): CostMode => {
    if (!isRecord(candidate)) throw new Error(`コストモード ${index + 1} が不正です。`)
    const range = candidate.range
    const sampleKpi = candidate.sampleKpi
    const colors = candidate.colors
    if (
      typeof candidate.id !== 'string' ||
      typeof candidate.displayName !== 'string' ||
      typeof candidate.description !== 'string' ||
      typeof candidate.unit !== 'string' ||
      typeof candidate.valueDirectionLabel !== 'string' ||
      (candidate.valueDirection !== 'higher-is-better' && candidate.valueDirection !== 'higher-is-worse') ||
      !isRecord(range) || !isFiniteNumber(range.min) || !isFiniteNumber(range.max) || range.min >= range.max ||
      !isRecord(sampleKpi) || typeof sampleKpi.label !== 'string' || !isFiniteNumber(sampleKpi.value) || typeof sampleKpi.unit !== 'string' ||
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
      unit: candidate.unit,
      range: { min: range.min, max: range.max },
      valueDirection: candidate.valueDirection,
      valueDirectionLabel: candidate.valueDirectionLabel,
      colors: parsedColors,
      sampleKpi: { label: sampleKpi.label, value: sampleKpi.value, unit: sampleKpi.unit },
    }
  })

  const modeIds = new Set(modes.map((mode) => mode.id))
  if (modeIds.size !== modes.length) throw new Error('コストモード ID が重複しています。')

  const features = value.features.map((candidate, index): RoadFeature => {
    if (!isRecord(candidate) || candidate.type !== 'Feature' || !isRecord(candidate.properties) || !isRecord(candidate.geometry)) {
      throw new Error(`道路 ${index + 1} が GeoJSON Feature ではありません。`)
    }
    const properties = candidate.properties
    const geometry = candidate.geometry
    if (typeof properties.id !== 'string' || typeof properties.name !== 'string' || !isRecord(properties.costs)) {
      throw new Error(`道路 ${index + 1} の属性が不正です。`)
    }
    if (geometry.type !== 'LineString' || !Array.isArray(geometry.coordinates) || geometry.coordinates.length < 2) {
      throw new Error(`道路 ${properties.id} の LineString が不正です。`)
    }
    const coordinates = geometry.coordinates.map((coordinate) => {
      if (!Array.isArray(coordinate) || coordinate.length < 2 || !isFiniteNumber(coordinate[0]) || !isFiniteNumber(coordinate[1])) {
        throw new Error(`道路 ${properties.id} の座標が不正です。`)
      }
      return [coordinate[0], coordinate[1]] as [number, number]
    })
    const costs: Record<string, number> = {}
    for (const mode of modes) {
      const cost = properties.costs[mode.id]
      if (!isFiniteNumber(cost) || cost < mode.range.min || cost > mode.range.max) {
        throw new Error(`道路 ${properties.id} の ${mode.displayName} 値が範囲外です。`)
      }
      costs[mode.id] = cost
    }
    return {
      type: 'Feature',
      properties: { id: properties.id, name: properties.name, costs },
      geometry: { type: 'LineString', coordinates },
    }
  })

  return {
    type: 'FeatureCollection',
    fixture: { isDummy: true, label: value.fixture.label, notice: value.fixture.notice },
    name: value.name,
    bbox: [minLng, minLat, maxLng, maxLat],
    costModes: modes,
    features,
  }
}

function formatValue(value: number, unit: string): string {
  return `${Number.isInteger(value) ? value : value.toFixed(2)}${unit}`
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

      <div class="concept-notice" role="note">
        <strong>デモ画面</strong>
        <span>環境コストと経路の値はダミーです。避難判断や安全保証には使用できません。</span>
      </div>

      <section class="mode-panel" aria-label="環境コストモード">
        <div>
          <p class="panel-label">環境コスト</p>
          <div class="mode-buttons" id="mode-buttons" aria-live="polite"></div>
        </div>
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
              <svg class="road-overlay" id="road-overlay" aria-label="ダミー道路レイヤー"></svg>
              <span class="map-data-label" id="map-data-label">道路レイヤー準備中</span>
              <div class="map-state" id="map-state" role="status">
                <span class="spinner" aria-hidden="true"></span>
                <strong>ダミー道路データを読み込んでいます</strong>
                <small>しばらくお待ちください</small>
              </div>
              <div class="basemap-warning" id="basemap-warning" hidden>背景地図を取得できません。道路データは引き続き操作できます。</div>
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
              <span class="preview-label">UIのみ</span>
            </div>
            <label>出発地<input value="東京駅 丸の内南口（例）" disabled></label>
            <label>目的地<input value="日比谷公園（例）" disabled></label>
            <div class="condition-row">
              <label>日付<input type="date" value="2026-08-22" disabled></label>
              <label>時刻<select disabled><option>12:00</option></select></label>
            </div>
            <button class="search-button" type="button" disabled>経路を比較（未実装）</button>
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
  if (kpiValue) kpiValue.textContent = formatValue(mode.sampleKpi.value, mode.sampleKpi.unit)
  if (legendRange) legendRange.textContent = `${mode.range.min}–${mode.range.max}${mode.unit}`
  if (legendList) {
    legendList.innerHTML = mode.colors.map((stop) => `
      <li><span class="legend-swatch" style="--swatch: ${stop.color}"></span><span>${escapeHtml(stop.label)}</span><strong>${formatValue(stop.value, mode.unit)}</strong></li>
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
  const container = mapInstance.getContainer()
  overlay.setAttribute('viewBox', `0 0 ${container.clientWidth} ${container.clientHeight}`)
  const roadPaths = fixture.features.map((feature) => {
    const path = feature.geometry.coordinates.map(([lng, lat], index) => {
      const point = mapInstance.project([lng, lat])
      return `${index === 0 ? 'M' : 'L'} ${point.x.toFixed(2)} ${point.y.toFixed(2)}`
    }).join(' ')
    const value = feature.properties.costs[mode.id]
    const label = `${feature.properties.name}: ${formatValue(value, mode.unit)}`
    return `<path class="road-overlay-line" d="${path}" stroke="${colorForValue(value, mode.colors)}"><title>${escapeHtml(label)}</title></path>`
  }).join('')
  const casingPaths = roadPaths.replaceAll('road-overlay-line', 'road-overlay-casing').replaceAll(/stroke="[^"]+"/g, 'stroke="#ffffff"')
  overlay.innerHTML = `<g>${casingPaths}</g><g>${roadPaths}</g>`
}

function initializeMap(): void {
  if (!fixture) return
  const mode = activeMode()
  const [minLng, minLat, maxLng, maxLat] = fixture.bbox

  const mapInstance = new MapLibreMap({
    container: 'map',
    style: baseStyle,
    center: [(minLng + maxLng) / 2, (minLat + maxLat) / 2],
    zoom: 15,
    attributionControl: false,
  })
  map = mapInstance
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
    mapInstance.fitBounds([[minLng, minLat], [maxLng, maxLat]], { padding: 48, duration: 0 })
    renderRoadOverlay(mapInstance, mode)
    document.querySelector<HTMLElement>('#map-state')?.setAttribute('hidden', '')
    const label = document.querySelector<HTMLElement>('#map-data-label')
    if (label) label.textContent = `${fixture.features.length}本のダミー道路を表示中`
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
    if (badge) badge.textContent = fixture.fixture.label
    if (notice) notice.textContent = fixture.fixture.notice
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
