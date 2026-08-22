import { defineConfig } from 'vite'

export default defineConfig({
  publicDir: '../data/fixtures',
  server: {
    host: '127.0.0.1',
    port: 8002,
    strictPort: true,
  },
  preview: {
    host: '127.0.0.1',
    port: 8002,
    strictPort: true,
  },
})
