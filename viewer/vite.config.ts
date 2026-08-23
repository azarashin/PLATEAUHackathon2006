import { copyFileSync, mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'
import { defineConfig, type Plugin } from 'vite'

const DEFAULT_PRODUCTION_BASE_PATH = '/environment-cost-route-finder/'
const VIEWER_ROOT = fileURLToPath(new URL('.', import.meta.url))
const MAPLIBRE_WORKER_ASSETS = ['maplibre-gl-worker.mjs', 'maplibre-gl-shared.mjs'] as const

function normalizeBasePath(value: string | undefined): string {
  if (!value || value === '/') return '/'
  const leadingSlash = value.startsWith('/') ? value : `/${value}`
  return leadingSlash.endsWith('/') ? leadingSlash : `${leadingSlash}/`
}

function copyMapLibreWorkerAssets(): Plugin {
  return {
    name: 'copy-maplibre-worker-assets',
    apply: 'build',
    closeBundle() {
      const outputDirectory = resolve(VIEWER_ROOT, 'dist/assets')
      const mapLibreDistribution = resolve(VIEWER_ROOT, 'node_modules/maplibre-gl/dist')
      mkdirSync(outputDirectory, { recursive: true })

      for (const asset of MAPLIBRE_WORKER_ASSETS) {
        copyFileSync(resolve(mapLibreDistribution, asset), resolve(outputDirectory, asset))
      }
    },
  }
}

export default defineConfig(({ command }) => ({
  base: normalizeBasePath(process.env.VIEWER_BASE_PATH ?? (command === 'build' ? DEFAULT_PRODUCTION_BASE_PATH : '/')),
  publicDir: '../data/fixtures',
  optimizeDeps: { exclude: ['maplibre-gl'] },
  plugins: [copyMapLibreWorkerAssets()],
}))
