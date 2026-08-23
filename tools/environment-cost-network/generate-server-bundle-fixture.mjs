import { fileURLToPath } from 'node:url'
import { buildServerBundleDocuments, writeServerBundle } from './build-environment-cost-server-bundle.mjs'
import { createFixtureInputs } from './fixture-inputs.mjs'

const outputDirectory = fileURLToPath(new URL('../../data/fixtures/environment-cost-server-bundle-v1/', import.meta.url))
const { graph, environment } = createFixtureInputs()
const bundle = buildServerBundleDocuments(graph, environment, {
  allowUnmatchedAsMissing: true,
  provenance: 'fixture',
})
const written = await writeServerBundle(outputDirectory, bundle)
console.log(`SERVER_BUNDLE_FIXTURE_GENERATED directory=${outputDirectory} files=${written.manifest.costSlices.length + 2} bytes=${written.totalBundleBytes} fingerprint=${written.manifest.bundleFingerprintSha256}`)
