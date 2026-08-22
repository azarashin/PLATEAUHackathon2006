import { defineConfig } from 'vite'

function normalizeBasePath(value: string | undefined): string {
  if (!value || value === '/') return '/'
  const leadingSlash = value.startsWith('/') ? value : `/${value}`
  return leadingSlash.endsWith('/') ? leadingSlash : `${leadingSlash}/`
}

export default defineConfig({
  base: normalizeBasePath(process.env.VIEWER_BASE_PATH),
  publicDir: '../data/fixtures',
})
