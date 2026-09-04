#!/usr/bin/env node

import { readFile, mkdir, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { buildGraph, graphFingerprint, qualityReport } from './build-sidewalk-pedestrian-graph.mjs'

function parseArgs(args) { const o = {}; for (let i = 0; i < args.length; i += 2) { if (!args[i]?.startsWith('--') || !args[i + 1]) throw new Error('expected --config --snapshot-manifest --report [--osm]'); o[args[i].slice(2)] = args[i + 1] } for (const k of ['config', 'snapshot-manifest', 'report']) if (!o[k]) throw new Error(`--${k} is required`); return o }
async function main() {
  const o = parseArgs(process.argv.slice(2)); const [config, manifest] = await Promise.all([readFile(resolve(o.config), 'utf8').then(JSON.parse), readFile(resolve(o['snapshot-manifest']), 'utf8').then(JSON.parse)])
  const base = { schemaVersion: 'regional-sidewalk-pedestrian-network-verification-0.2', verifiedAt: new Date().toISOString(), areaId: config.areaId, captureManifest: { schemaVersion: manifest.schemaVersion ?? null, captureContractVersion: manifest.captureContractVersion ?? null, sha256: manifest.sha256 ?? null } }
  if (manifest.captureContractVersion !== '0.2') {
    const report = { ...base, status: 'blocked', reason: 'capture-contract-0.2-missing', recommendedVersion: 'v1', validation: { graphBuilt: false, reason: 'The current regional snapshot is a way-only v0.1 capture and must not be used for a v2 sidewalk graph.' } }
    await mkdir(dirname(resolve(o.report)), { recursive: true }); await writeFile(resolve(o.report), `${JSON.stringify(report, null, 2)}\n`); console.log(`SIDEWALK_REGIONAL_VERIFICATION_BLOCKED area=${config.areaId} reason=capture-contract-0.2-missing`); return
  }
  if (!o.osm) throw new Error('--osm is required for capture contract 0.2')
  const graph = buildGraph(config, JSON.parse(await readFile(resolve(o.osm), 'utf8'))), quality = qualityReport(graph, { areaId: config.areaId, captureContractVersion: '0.2', requireRepresentativeOds: true }, config.representativeOds)
  const report = { ...base, status: quality.validation.status, graphFingerprintSha256: graphFingerprint(graph), quality }
  await mkdir(dirname(resolve(o.report)), { recursive: true }); await writeFile(resolve(o.report), `${JSON.stringify(report, null, 2)}\n`); if (quality.validation.status === 'rejected') throw new Error(quality.validation.rejectedReasons.join(', ')); console.log(`SIDEWALK_REGIONAL_VERIFIED area=${config.areaId} status=${quality.validation.status}`)
}
main().catch((e) => { console.error(e.message); process.exitCode = 1 })
