#!/usr/bin/env node

import { readFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import Ajv2020 from 'ajv/dist/2020.js'
import addFormats from 'ajv-formats'

const schemaPath = fileURLToPath(new URL('../../schemas/environment-cost-road-network-v1.schema.json', import.meta.url))
const schema = JSON.parse(await readFile(schemaPath, 'utf8'))
const ajv = new Ajv2020({ allErrors: true, strict: true })
addFormats(ajv)
const validateSchema = ajv.compile(schema)

function equalCoordinate(left, right) {
  return Array.isArray(left) && Array.isArray(right) && left.length === 2 && right.length === 2 &&
    Math.abs(left[0] - right[0]) <= 1e-10 && Math.abs(left[1] - right[1]) <= 1e-10
}

function duplicateIds(items) {
  const seen = new Set()
  return items.map((item) => item.id).filter((id) => seen.has(id) || !seen.add(id))
}

function semanticErrors(document) {
  const errors = []
  const [minLongitude, minLatitude, maxLongitude, maxLatitude] = document.area.bbox
  if (minLongitude >= maxLongitude || minLatitude >= maxLatitude) errors.push('bbox min values must be smaller than max values')

  for (const id of duplicateIds(document.costDefinitions)) errors.push(`duplicate cost definition id: ${id}`)
  for (const id of duplicateIds(document.nodes)) errors.push(`duplicate node id: ${id}`)
  for (const id of duplicateIds(document.edges)) errors.push(`duplicate edge id: ${id}`)

  const definitions = new Map(document.costDefinitions.map((definition) => [definition.id, definition]))
  for (const definition of document.costDefinitions) {
    if (definition.range.min >= definition.range.max) errors.push(`invalid range for cost definition: ${definition.id}`)
    for (const stop of definition.presentation.colors) {
      if (stop.value < definition.range.min || stop.value > definition.range.max) errors.push(`color stop out of range: ${definition.id}`)
    }
  }

  const registeredTimestamps = new Set(document.scenario.availableTimestamps)
  if (!registeredTimestamps.has(document.scenario.defaultTimestamp)) errors.push('default timestamp is not registered')
  for (const timestamp of registeredTimestamps) {
    if (!timestamp.startsWith(`${document.scenario.referenceDate}T`)) errors.push(`timestamp is outside reference date: ${timestamp}`)
  }

  const nodes = new Map(document.nodes.map((node) => [node.id, node]))
  for (const edge of document.edges) {
    const fromNode = nodes.get(edge.fromNodeId)
    const toNode = nodes.get(edge.toNodeId)
    if (!fromNode) errors.push(`missing node reference: ${edge.id}.fromNodeId=${edge.fromNodeId}`)
    if (!toNode) errors.push(`missing node reference: ${edge.id}.toNodeId=${edge.toNodeId}`)
    if (fromNode && !equalCoordinate(edge.geometry.coordinates[0], fromNode.coordinate)) errors.push(`geometry start does not match from node: ${edge.id}`)
    if (toNode && !equalCoordinate(edge.geometry.coordinates.at(-1), toNode.coordinate)) errors.push(`geometry end does not match to node: ${edge.id}`)

    const timestamps = edge.timeSlices.map((slice) => slice.timestamp)
    if (new Set(timestamps).size !== timestamps.length) errors.push(`duplicate timestamp on edge: ${edge.id}`)
    for (const timestamp of registeredTimestamps) {
      if (!timestamps.includes(timestamp)) errors.push(`missing registered timestamp: ${edge.id} ${timestamp}`)
    }
    for (const slice of edge.timeSlices) {
      if (!registeredTimestamps.has(slice.timestamp)) errors.push(`unregistered timestamp: ${edge.id} ${slice.timestamp}`)
      const coverage = slice.sampleCoverage
      if (coverage.validSampleCount + coverage.noGroundSampleCount !== coverage.sampleCount) errors.push(`sample coverage total mismatch: ${edge.id} ${slice.timestamp}`)
      const valueIds = Object.keys(slice.values)
      for (const definitionId of definitions.keys()) {
        if (!Object.hasOwn(slice.values, definitionId)) errors.push(`missing cost value: ${edge.id} ${slice.timestamp} ${definitionId}`)
      }
      for (const valueId of valueIds) {
        const definition = definitions.get(valueId)
        if (!definition) {
          errors.push(`undefined cost value: ${edge.id} ${slice.timestamp} ${valueId}`)
          continue
        }
        const value = slice.values[valueId]
        if (value !== null && (value < definition.range.min || value > definition.range.max)) errors.push(`value out of range: ${edge.id} ${slice.timestamp} ${valueId}`)
      }

      const values = Object.values(slice.values)
      if (slice.status === 'missing') {
        if (coverage.validSampleCount !== 0 || values.some((value) => value !== null)) errors.push(`missing values must be null: ${edge.id} ${slice.timestamp}`)
      } else if (slice.status === 'available') {
        if (coverage.validSampleCount === 0 || values.some((value) => value === null)) errors.push(`available values must be numbers: ${edge.id} ${slice.timestamp}`)
        if (coverage.noGroundSampleCount !== 0) errors.push(`available slice cannot contain no-ground samples: ${edge.id} ${slice.timestamp}`)
      } else {
        if (coverage.validSampleCount === 0 || values.every((value) => value === null)) errors.push(`partial slice must contain a calculated value: ${edge.id} ${slice.timestamp}`)
        if (coverage.noGroundSampleCount === 0) errors.push(`partial slice must contain no-ground samples: ${edge.id} ${slice.timestamp}`)
      }

      const shadeRatio = slice.values.shadeRatio
      const solarExposureSeconds = slice.values.solarExposureSeconds
      if (typeof shadeRatio === 'number' && typeof solarExposureSeconds === 'number') {
        const expectedExposure = edge.walkingSeconds * (1 - shadeRatio)
        if (Math.abs(expectedExposure - solarExposureSeconds) > 1e-5) errors.push(`solar exposure invariant mismatch: ${edge.id} ${slice.timestamp}`)
      }
    }
  }
  return errors
}

function validateDocument(document) {
  const validSchema = validateSchema(document)
  const errors = validSchema ? [] : (validateSchema.errors ?? []).map((error) => `schema ${error.instancePath || '/'} ${error.message}`)
  if (validSchema) errors.push(...semanticErrors(document))
  return errors
}

async function main() {
  const paths = process.argv.slice(2)
  if (paths.length === 0) throw new Error('Pass one or more JSON files to validate.')
  let failed = false
  for (const path of paths) {
    const document = JSON.parse(await readFile(resolve(path), 'utf8'))
    const errors = validateDocument(document)
    if (errors.length === 0) console.log(`CONTRACT_VALID ${path}`)
    else {
      failed = true
      console.error(`CONTRACT_INVALID ${path}`)
      for (const error of errors) console.error(`- ${error}`)
    }
  }
  if (failed) process.exitCode = 1
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(error.message)
    process.exitCode = 1
  })
}

export { validateDocument }
