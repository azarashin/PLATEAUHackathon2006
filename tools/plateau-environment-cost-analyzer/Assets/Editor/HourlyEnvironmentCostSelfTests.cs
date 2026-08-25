using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class HourlyEnvironmentCostSelfTests
{
    public static void Run()
    {
        try
        {
            AssertNear(75.0, HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(100.0, 0.25));
            AssertNear(100.0, HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(100.0, 0.0));
            AssertNear(0.0, HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(100.0, 1.0));
            AssertEqual("available", HourlyEnvironmentCostRules.DetermineStatus(4, 4, 0, 60.0, out var availableReason));
            AssertEqual(null, availableReason);
            AssertEqual("partial", HourlyEnvironmentCostRules.DetermineStatus(4, 3, 1, 60.0, out var partialReason));
            AssertEqual("some-road-samples-not-found", partialReason);
            AssertEqual("missing", HourlyEnvironmentCostRules.DetermineStatus(4, 0, 4, 60.0, out var missingReason));
            AssertEqual("road-surface-not-found", missingReason);
            AssertEqual("missing", HourlyEnvironmentCostRules.DetermineStatus(0, 0, 0, 60.0, out var zeroSampleReason));
            AssertEqual("road-surface-not-found", zeroSampleReason);
            AssertEqual("missing", HourlyEnvironmentCostRules.DetermineStatus(4, 4, 0, -1.0, out var nightReason));
            AssertEqual("sun-below-horizon", nightReason);
            AssertEqual("2025-08-01T08:00:00+09:00",
                HourlyEnvironmentCostRules.Timestamp(new DateTime(2025, 8, 1), 8, "Asia/Tokyo"));
            var noonSun = HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 12,
                35.6916, 139.7365, "Asia/Tokyo");
            var nightSun = HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 0,
                35.6916, 139.7365, "Asia/Tokyo");
            if (noonSun.elevationDegrees <= 0.0) throw new InvalidOperationException("Expected the noon sun to be above the horizon.");
            if (nightSun.elevationDegrees >= 0.0) throw new InvalidOperationException("Expected the midnight sun to be below the horizon.");
            AssertNear(1.0, noonSun.direction.magnitude);
            AssertNear(1.0, nightSun.direction.magnitude);
            // NOAA's general solar-position equations, using Ichigaya's analysis centre and JST.
            var referenceMorning = HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 8.0,
                35.690470, 139.736043, "Asia/Tokyo");
            var referenceNoon = HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 12.0,
                35.690470, 139.736043, "Asia/Tokyo");
            AssertNear(37.166051, referenceMorning.elevationDegrees, 0.05);
            AssertNear(93.458541, referenceMorning.azimuthDegrees, 0.05);
            AssertNear(72.317290, referenceNoon.elevationDegrees, 0.05);
            AssertNear(189.768435, referenceNoon.azimuthDegrees, 0.05);
            AssertThrows<ArgumentOutOfRangeException>(() => HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(100.0, 1.1));
            AssertThrows<ArgumentException>(() => HourlyEnvironmentCostRules.DetermineStatus(4, 2, 1, 60.0, out _));
            AssertThrows<ArgumentOutOfRangeException>(() => HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 24,
                35.6916, 139.7365, "Asia/Tokyo"));
            AssertThrows<ArgumentOutOfRangeException>(() => HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 24.0,
                35.6916, 139.7365, "Asia/Tokyo"));
            AssertGridCodes(new[] { "53396530", "53396531" }, MeshCoverageAnalyzer.NormalizeGridCodes(new[] { "533965", "53396530", "53396531" }));
            AssertGridCodes(new[] { "533974", "533975" }, MeshCoverageAnalyzer.NormalizeGridCodes(new[] { "533974", "533975" }));
            AssertGridCodes(new[] { "533965", "53396530" }, MeshCoverageAnalyzer.NormalizeGridCodes(new[] { "533965", "invalid", "53396530", "53396530" }));
            Debug.Log("HOURLY_ENVIRONMENT_COST_SELF_TEST_PASSED");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static void AssertNear(double expected, double actual)
    {
        AssertNear(expected, actual, HourlyEnvironmentCostRules.FormulaToleranceSeconds);
    }

    private static void AssertNear(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }

    private static void AssertEqual(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected {expected ?? "<null>"}, actual {actual ?? "<null>"}.");
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected exception {typeof(T).Name}.");
    }

    private static void AssertGridCodes(string[] expected, System.Collections.Generic.List<string> actual)
    {
        if (expected.Length != actual.Count || !expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected normalized grid codes: {string.Join(",", actual)}.");
        }
    }
}
