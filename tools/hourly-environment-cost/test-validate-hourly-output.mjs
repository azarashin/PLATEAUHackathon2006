import assert from "node:assert/strict";
import { validateHourlyOutput } from "./validate-hourly-output.mjs";

function fixture() {
  const hours = [8, 9];
  const edge = {
    id: "way-1:1-2",
    walkingSeconds: 100,
    sampleCount: 2,
    validSampleCount: 1,
    noGroundSampleCount: 1,
    hourly: hours.map((hour) => ({
      hour,
      timestamp: `2025-08-01T${String(hour).padStart(2, "0")}:00:00+09:00`,
      status: "partial",
      exclusionReason: "some-road-samples-not-found",
      sunElevationDegrees: 20,
      shadeRatio: 0.25,
      solarExposureSeconds: 75,
    })),
  };
  return {
    schemaVersion: "environment-cost-analysis-0.2",
    status: "completed",
    resultFingerprintSha256: "a".repeat(64),
    settings: { hours },
    edges: [edge],
  };
}

const valid = fixture();
const summary = validateHourlyOutput(valid);
assert.equal(summary.edgeCount, 1);
assert.equal(summary.byHour[8].partial, 1);

const boundaryValues = fixture();
boundaryValues.edges[0].hourly[0].shadeRatio = 0;
boundaryValues.edges[0].hourly[0].solarExposureSeconds = 100;
boundaryValues.edges[0].hourly[1].shadeRatio = 1;
boundaryValues.edges[0].hourly[1].solarExposureSeconds = 0;
assert.doesNotThrow(() => validateHourlyOutput(boundaryValues), "shadeRatio boundaries 0 and 1 must be valid");

const zeroValidSamples = fixture();
zeroValidSamples.edges[0].validSampleCount = 0;
zeroValidSamples.edges[0].noGroundSampleCount = 2;
for (const slice of zeroValidSamples.edges[0].hourly) {
  slice.status = "missing";
  slice.exclusionReason = "road-surface-not-found";
  slice.shadeRatio = null;
  slice.solarExposureSeconds = null;
}
assert.equal(validateHourlyOutput(zeroValidSamples).byHour[8].missing, 1);

const incomplete = fixture();
incomplete.edges[0].hourly.pop();
assert.throws(() => validateHourlyOutput(incomplete), /hourly slice count mismatch/);

const formulaMismatch = fixture();
formulaMismatch.edges[0].hourly[0].solarExposureSeconds = 74;
assert.throws(() => validateHourlyOutput(formulaMismatch), /formula mismatch/);

const missingReason = fixture();
missingReason.edges[0].hourly[0].exclusionReason = null;
assert.throws(() => validateHourlyOutput(missingReason), /exclusion reason mismatch/);

console.log("HOURLY_OUTPUT_VALIDATOR_TEST_PASSED");
