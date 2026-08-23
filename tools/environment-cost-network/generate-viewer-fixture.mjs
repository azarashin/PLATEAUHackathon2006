import { mkdir, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildEnvironmentCostRoadNetwork } from './build-environment-cost-road-network.mjs'
import { createFixtureInputs } from './fixture-inputs.mjs'

const outputPath = fileURLToPath(new URL('../../data/fixtures/environment-cost-road-network-integration-v1.json', import.meta.url))
const { graph, environment } = createFixtureInputs()
const { document } = buildEnvironmentCostRoadNetwork(graph, environment, {
  allowUnmatchedAsMissing: true,
  provenance: 'fixture',
})
const { validateDocument } = await import('../../viewer/scripts/validate-environment-cost-data.mjs')
const errors = validateDocument(document)
if (errors.length > 0) throw new Error(`Generated fixture is invalid: ${errors.join('; ')}`)
await mkdir(dirname(outputPath), { recursive: true })
await writeFile(resolve(outputPath), `${JSON.stringify(document, null, 2)}\n`)
console.log(`VIEWER_INTEGRATION_FIXTURE_GENERATED path=${outputPath} nodes=${document.nodes.length} edges=${document.edges.length} bytes=${Buffer.byteLength(`${JSON.stringify(document, null, 2)}\n`)}`)
