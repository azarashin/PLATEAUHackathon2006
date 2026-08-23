import fs from "node:fs";
import { pathToFileURL } from "node:url";

const FORMULA_TOLERANCE_SECONDS = 1e-6;

function invariant(condition, message) {
  if (!condition) throw new Error(message);
}

export function validateHourlyOutput(document) {
  invariant(document?.schemaVersion === "environment-cost-analysis-0.2", "schemaVersion must be environment-cost-analysis-0.2");
  invariant(document.status === "completed", "status must be completed");
  invariant(typeof document.resultFingerprintSha256 === "string" && /^[0-9a-f]{64}$/.test(document.resultFingerprintSha256), "result fingerprint is missing or invalid");

  const hours = document.settings?.hours;
  invariant(Array.isArray(hours) && hours.length > 0, "settings.hours must not be empty");
  invariant(new Set(hours).size === hours.length, "settings.hours must be unique");
  invariant(hours.every((hour, index) => index === 0 || hours[index - 1] < hour), "settings.hours must be ascending");
  invariant(Array.isArray(document.edges) && document.edges.length > 0, "edges must not be empty");

  const edgeIds = new Set();
  const byHour = Object.fromEntries(hours.map((hour) => [hour, { available: 0, partial: 0, missing: 0 }]));
  let sampleCount = 0;
  let validSampleCount = 0;
  let noGroundSampleCount = 0;

  for (const edge of document.edges) {
    invariant(typeof edge.id === "string" && edge.id.length > 0, "edge id is missing");
    invariant(!edgeIds.has(edge.id), `duplicate edge id: ${edge.id}`);
    edgeIds.add(edge.id);
    invariant(edge.sampleCount > 0, `sampleCount must be positive: ${edge.id}`);
    invariant(edge.validSampleCount + edge.noGroundSampleCount === edge.sampleCount, `sample counts are inconsistent: ${edge.id}`);
    invariant(edge.walkingSeconds > 0, `walkingSeconds must be positive: ${edge.id}`);
    invariant(Array.isArray(edge.hourly) && edge.hourly.length === hours.length, `hourly slice count mismatch: ${edge.id}`);

    sampleCount += edge.sampleCount;
    validSampleCount += edge.validSampleCount;
    noGroundSampleCount += edge.noGroundSampleCount;

    edge.hourly.forEach((slice, index) => {
      const hour = hours[index];
      invariant(slice.hour === hour, `hourly slices are incomplete or unordered: ${edge.id}`);
      invariant(typeof slice.timestamp === "string" && !Number.isNaN(Date.parse(slice.timestamp)), `timestamp is invalid: ${edge.id} ${hour}`);

      let expectedStatus;
      let expectedReason = null;
      if (slice.sunElevationDegrees <= 0) {
        expectedStatus = "missing";
        expectedReason = "sun-below-horizon";
      } else if (edge.validSampleCount === 0) {
        expectedStatus = "missing";
        expectedReason = "road-surface-not-found";
      } else if (edge.noGroundSampleCount > 0) {
        expectedStatus = "partial";
        expectedReason = "some-road-samples-not-found";
      } else {
        expectedStatus = "available";
      }

      invariant(slice.status === expectedStatus, `status mismatch: ${edge.id} ${hour}`);
      invariant((slice.exclusionReason ?? null) === expectedReason, `exclusion reason mismatch: ${edge.id} ${hour}`);
      byHour[hour][slice.status] += 1;

      if (slice.status === "missing") {
        invariant(slice.shadeRatio === null && slice.solarExposureSeconds === null, `missing slice must contain null values: ${edge.id} ${hour}`);
        return;
      }

      invariant(Number.isFinite(slice.shadeRatio) && slice.shadeRatio >= 0 && slice.shadeRatio <= 1, `shadeRatio is invalid: ${edge.id} ${hour}`);
      invariant(Number.isFinite(slice.solarExposureSeconds), `solarExposureSeconds is invalid: ${edge.id} ${hour}`);
      const expectedExposure = edge.walkingSeconds * (1 - slice.shadeRatio);
      invariant(Math.abs(expectedExposure - slice.solarExposureSeconds) <= FORMULA_TOLERANCE_SECONDS, `solar exposure formula mismatch: ${edge.id} ${hour}`);
    });
  }

  return {
    edgeCount: document.edges.length,
    hourCount: hours.length,
    sampleCount,
    validSampleCount,
    noGroundSampleCount,
    resultFingerprintSha256: document.resultFingerprintSha256,
    byHour,
  };
}

export function validateHourlyOutputFile(path) {
  const document = JSON.parse(fs.readFileSync(path, "utf8"));
  return validateHourlyOutput(document);
}

const invokedPath = process.argv[1] ? pathToFileURL(process.argv[1]).href : null;
if (invokedPath === import.meta.url) {
  const outputPath = process.argv[2];
  if (!outputPath) {
    console.error("Usage: node --max-old-space-size=4096 validate-hourly-output.mjs <analysis-output.json>");
    process.exitCode = 2;
  } else {
    console.log(JSON.stringify(validateHourlyOutputFile(outputPath), null, 2));
  }
}
