#!/usr/bin/env node

import { readFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import Ajv2020 from 'ajv/dist/2020.js'
import addFormats from 'ajv-formats'
import {
  loadEnvironmentCostServerBundle,
  safeReferencedPath,
} from '../../tools/environment-cost-network/load-environment-cost-server-bundle.mjs'

const schemaUrls = {
  v1: {
    manifest: new URL('../../schemas/environment-cost-server-bundle-v1.schema.json', import.meta.url), topology: new URL('../../schemas/environment-cost-server-topology-v1.schema.json', import.meta.url), cost: new URL('../../schemas/environment-cost-server-cost-slice-v1.schema.json', import.meta.url),
  },
  v2: {
    manifest: new URL('../../schemas/environment-cost-server-bundle-v2.schema.json', import.meta.url), topology: new URL('../../schemas/environment-cost-server-topology-v2.schema.json', import.meta.url), cost: new URL('../../schemas/environment-cost-server-cost-slice-v2.schema.json', import.meta.url),
  },
}
const ajv = new Ajv2020({ allErrors: true, strict: true })
addFormats(ajv)
const validators = Object.fromEntries(await Promise.all(Object.entries(schemaUrls).map(async ([version, urls]) => [version, Object.fromEntries(await Promise.all(Object.entries(urls).map(async ([name, url]) => [name, ajv.compile(JSON.parse(await readFile(fileURLToPath(url), 'utf8')))])))])))

function schemaErrors(validate, document, label) {
  if (validate(document)) return []
  return (validate.errors ?? []).map((error) => `${label} ${error.instancePath || '/'} ${error.message}`)
}

async function validateBundle(path) {
  const absoluteManifestPath = resolve(path)
  const directory = dirname(absoluteManifestPath)
  const manifest = JSON.parse(await readFile(absoluteManifestPath, 'utf8'))
  const version = manifest.schemaVersion === 'environment-cost-server-bundle-2.0' ? 'v2' : 'v1'
  const selected = validators[version]
  const errors = schemaErrors(selected.manifest, manifest, 'manifest')
  if (errors.length > 0) throw new Error(errors.slice(0, 50).join('; '))
  const topology = JSON.parse(await readFile(safeReferencedPath(directory, manifest.topology.file), 'utf8'))
  errors.push(...schemaErrors(selected.topology, topology, 'topology'))
  for (const reference of manifest.costSlices) {
    const cost = JSON.parse(await readFile(safeReferencedPath(directory, reference.file), 'utf8'))
    errors.push(...schemaErrors(selected.cost, cost, reference.file))
  }
  if (errors.length > 0) throw new Error(errors.slice(0, 50).join('; '))
  const runtime = await loadEnvironmentCostServerBundle(absoluteManifestPath)
  return {
    nodeCount: runtime.nodeSourceIds.length,
    physicalEdgeCount: runtime.physicalEdgeIds.length,
    directedEdgeCount: runtime.directedPhysicalIndexes.length,
    hourCount: runtime.costsByTimestamp.size,
    bundleFingerprintSha256: runtime.manifest.bundleFingerprintSha256,
  }
}

async function main() {
  const paths = process.argv.slice(2)
  if (paths.length === 0) throw new Error('Pass one or more server bundle manifest files to validate.')
  for (const path of paths) {
    const result = await validateBundle(path)
    console.log(`SERVER_BUNDLE_VALID ${path} nodes=${result.nodeCount} physicalEdges=${result.physicalEdgeCount} directedEdges=${result.directedEdgeCount} hours=${result.hourCount} fingerprint=${result.bundleFingerprintSha256}`)
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message)
    process.exitCode = 1
  })
}

export { validateBundle }
