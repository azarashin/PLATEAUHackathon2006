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
  fixture: { isDummy: boolean; label: string; notice: string }
  name: string
  bbox: [number, number, number, number]
  costModes: CostMode[]
  features: RoadFeature[]
}

const appElement = document.querySelector<HTMLDivElement>('#app')

if (!appElement) {
  throw new Error('Application root #app was not found.')
}

const app: HTMLDivElement = appElement

const fixtureUrl = '/environment-costs-phase-a.geojson'

function escapeHtml(value: string): string {
  return value.replace(/[&<>"]/g, (character) => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
  })[character] ?? character)
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

function pointToSvg(
  coordinate: [number, number],
  bbox: EnvironmentCostsFixture['bbox'],
): [number, number] {
  const [minX, minY, maxX, maxY] = bbox
  const padding = 7
  const x = padding + ((coordinate[0] - minX) / (maxX - minX)) * (100 - padding * 2)
  const y = 100 - padding - ((coordinate[1] - minY) / (maxY - minY)) * (100 - padding * 2)
  return [x, y]
}

function roadPath(feature: RoadFeature, bbox: EnvironmentCostsFixture['bbox']): string {
  return feature.geometry.coordinates
    .map((coordinate, index) => {
      const [x, y] = pointToSvg(coordinate, bbox)
      return `${index === 0 ? 'M' : 'L'} ${x.toFixed(2)} ${y.toFixed(2)}`
    })
    .join(' ')
}

function formatValue(value: number, unit: string): string {
  return `${Number.isInteger(value) ? value : value.toFixed(2)}${unit}`
}

function render(fixture: EnvironmentCostsFixture, selectedModeId: string): void {
  const selectedMode = fixture.costModes.find((mode) => mode.id === selectedModeId) ?? fixture.costModes[0]
  if (!selectedMode) throw new Error('コストモードが定義されていません。')

  const modeButtons = fixture.costModes.map((mode) => `
    <button class="mode-button${mode.id === selectedMode.id ? ' is-active' : ''}" type="button"
      data-mode-id="${escapeHtml(mode.id)}" aria-pressed="${mode.id === selectedMode.id}">
      <span>${escapeHtml(mode.displayName)}</span>
      <small>${escapeHtml(mode.valueDirectionLabel)}</small>
    </button>
  `).join('')

  const roads = fixture.features.map((feature) => {
    const value = feature.properties.costs[selectedMode.id]
    const color = colorForValue(value, selectedMode.colors)
    const label = `${feature.properties.name}: ${formatValue(value, selectedMode.unit)}`
    return `<path class="road" d="${roadPath(feature, fixture.bbox)}" stroke="${color}" tabindex="0" aria-label="${escapeHtml(label)}"><title>${escapeHtml(label)}</title></path>`
  }).join('')

  const legend = selectedMode.colors.map((stop) => `
    <li><span class="legend__swatch" style="--swatch: ${stop.color}"></span><span>${escapeHtml(stop.label)}</span><strong>${formatValue(stop.value, selectedMode.unit)}</strong></li>
  `).join('')

  app.innerHTML = `
    <main class="viewer-shell">
      <header class="topbar">
        <div>
          <p class="eyebrow">Environmental Cost Route Map</p>
          <h1>環境コストマップ</h1>
        </div>
        <span class="dummy-badge">${escapeHtml(fixture.fixture.label)}</span>
      </header>

      <section class="mode-panel" aria-label="コストモード">
        <div class="mode-buttons">${modeButtons}</div>
      </section>

      <section class="viewer-grid">
        <div class="map-card">
          <div class="map-heading">
            <div>
              <p class="eyebrow">Map preview</p>
              <h2>${escapeHtml(selectedMode.displayName)}コスト</h2>
            </div>
            <span class="direction direction--${selectedMode.valueDirection}">${escapeHtml(selectedMode.valueDirectionLabel)}</span>
          </div>
          <div class="map" role="img" aria-label="${escapeHtml(selectedMode.displayName)}コストで色分けしたダミー道路地図">
            <svg viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
              <g class="blocks">
                <rect x="10" y="10" width="25" height="22" rx="2" />
                <rect x="42" y="8" width="20" height="26" rx="2" />
                <rect x="70" y="12" width="20" height="24" rx="2" />
                <rect x="12" y="54" width="24" height="30" rx="2" />
                <rect x="45" y="48" width="19" height="38" rx="2" />
                <rect x="72" y="50" width="18" height="32" rx="2" />
              </g>
              <g class="roads">${roads}</g>
            </svg>
            <span class="map-label">架空エリア / 5道路</span>
          </div>
        </div>

        <aside class="details-card">
          <p class="eyebrow">Mode detail</p>
          <h2>${escapeHtml(selectedMode.displayName)}</h2>
          <p class="description">${escapeHtml(selectedMode.description)}</p>

          <div class="kpi">
            <span>${escapeHtml(selectedMode.sampleKpi.label)}</span>
            <strong>${formatValue(selectedMode.sampleKpi.value, selectedMode.sampleKpi.unit)}</strong>
            <small>サンプル KPI</small>
          </div>

          <div class="legend">
            <div class="legend__title"><span>凡例</span><small>${selectedMode.range.min}–${selectedMode.range.max}${escapeHtml(selectedMode.unit)}</small></div>
            <ul>${legend}</ul>
          </div>

          <p class="fixture-notice">${escapeHtml(fixture.fixture.notice)}</p>
        </aside>
      </section>
    </main>
  `

  app.querySelectorAll<HTMLButtonElement>('[data-mode-id]').forEach((button) => {
    button.addEventListener('click', () => render(fixture, button.dataset.modeId ?? selectedMode.id))
  })
}

async function start(): Promise<void> {
  app.innerHTML = '<p class="loading" role="status">ダミー環境コストを読み込んでいます…</p>'

  try {
    const response = await fetch(fixtureUrl)
    if (!response.ok) throw new Error(`fixture の取得に失敗しました (${response.status})`)
    const fixture = await response.json() as EnvironmentCostsFixture
    render(fixture, fixture.costModes[0]?.id ?? '')
  } catch (error) {
    const message = error instanceof Error ? error.message : '不明なエラー'
    app.innerHTML = `<p class="error" role="alert">データを読み込めませんでした。${escapeHtml(message)}</p>`
  }
}

void start()
