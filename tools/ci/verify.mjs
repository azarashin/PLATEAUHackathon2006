#!/usr/bin/env node

import { spawnSync } from 'node:child_process'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')
const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm'
const steps = [
  ['道路グラフ', process.execPath, ['tools/road-network/test-build-pedestrian-graph.mjs']],
  ['時間別環境コスト', process.execPath, ['tools/hourly-environment-cost/test-validate-hourly-output.mjs']],
  ['座標変換', process.execPath, ['tools/environment-cost-network/test-japan-plane-rectangular.mjs']],
  ['環境コスト結合', process.execPath, ['tools/environment-cost-network/test-build-environment-cost-road-network.mjs']],
  ['サーバーバンドル', process.execPath, ['tools/environment-cost-network/test-environment-cost-server-bundle.mjs']],
  ['経路サーバー', npm, ['--prefix', 'server', 'test']],
  ['Viewer・データ契約', npm, ['--prefix', 'viewer', 'run', 'verify']],
]

for (const [label, command, args] of steps) {
  console.log(`\n=== VERIFY ${label} ===`)
  const result = spawnSync(command, args, {
    cwd: repositoryRoot,
    env: { ...process.env, CI: 'true' },
    shell: process.platform === 'win32' && command === npm,
    stdio: 'inherit',
  })
  if (result.error) {
    console.error(`VERIFY_STEP_FAILED label=${label} error=${result.error.message}`)
    process.exit(1)
  }
  if (result.status !== 0) {
    console.error(`VERIFY_STEP_FAILED label=${label} exitCode=${result.status ?? 'signal'}`)
    process.exit(result.status ?? 1)
  }
}

console.log('\nVERIFY_ALL_PASSED')
