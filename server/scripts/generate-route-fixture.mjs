import { fileURLToPath } from 'node:url'
import { buildServerBundleDocuments, writeServerBundle } from '../../tools/environment-cost-network/build-environment-cost-server-bundle.mjs'
import { createRouteFixtureInputs } from '../fixtures/route-fixture-inputs.mjs'

const outputDirectory = fileURLToPath(new URL('../../data/fixtures/route-server-bundle-v1/', import.meta.url))
const { graph, environment } = createRouteFixtureInputs()
const bundle = buildServerBundleDocuments(graph, environment, { provenance: 'fixture' })
const written = await writeServerBundle(outputDirectory, bundle)
console.log(`ROUTE_SERVER_FIXTURE_GENERATED directory=${outputDirectory} files=${written.manifest.costSlices.length + 2} bytes=${written.totalBundleBytes} fingerprint=${written.manifest.bundleFingerprintSha256}`)
