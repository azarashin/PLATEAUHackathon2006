import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { validateDocument } from './validate-environment-cost-data.mjs'

const validPath = fileURLToPath(new URL('../../data/fixtures/environment-cost-road-network-v1.json', import.meta.url))
const casesPath = fileURLToPath(new URL('../../data/fixtures/invalid/environment-cost-road-network-v1-cases.json', import.meta.url))
const validDocument = JSON.parse(await readFile(validPath, 'utf8'))
const invalidCases = JSON.parse(await readFile(casesPath, 'utf8'))

function valueAtPointer(document, pointer) {
  return pointer.split('/').slice(1).reduce((value, segment) => value[segment.replaceAll('~1', '/').replaceAll('~0', '~')], document)
}

function replaceAtPointer(document, pointer, value) {
  const segments = pointer.split('/').slice(1).map((segment) => segment.replaceAll('~1', '/').replaceAll('~0', '~'))
  const property = segments.pop()
  const parent = segments.reduce((current, segment) => current[segment], document)
  parent[property] = value
}

function applyMutation(document, mutation) {
  if (mutation.operation === 'replace') replaceAtPointer(document, mutation.path, mutation.value)
  else if (mutation.operation === 'copy') replaceAtPointer(document, mutation.path, structuredClone(valueAtPointer(document, mutation.from)))
  else throw new Error(`Unsupported mutation operation: ${mutation.operation}`)
}

assert.deepEqual(validateDocument(validDocument), [], 'normal fixture must pass the formal contract')

for (const testCase of invalidCases.cases) {
  const document = structuredClone(validDocument)
  applyMutation(document, testCase.mutation)
  const errors = validateDocument(document)
  assert.ok(errors.some((error) => error.includes(testCase.expectedError)), `${testCase.id} did not report ${testCase.expectedError}: ${errors.join('; ')}`)
}

console.log(`CONTRACT_TEST_PASSED invalidCases=${invalidCases.cases.length}`)
