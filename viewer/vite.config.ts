import { defineConfig } from 'vite'

const DEFAULT_PRODUCTION_BASE_PATH = '/environment-cost-route-finder/'

function normalizeBasePath(value: string | undefined): string {
  if (!value || value === '/') return '/'
  const leadingSlash = value.startsWith('/') ? value : `/${value}`
  return leadingSlash.endsWith('/') ? leadingSlash : `${leadingSlash}/`
}

export default defineConfig(({ command }) => ({
  base: normalizeBasePath(process.env.VIEWER_BASE_PATH ?? (command === 'build' ? DEFAULT_PRODUCTION_BASE_PATH : '/')),
  publicDir: '../data/fixtures',
}))
