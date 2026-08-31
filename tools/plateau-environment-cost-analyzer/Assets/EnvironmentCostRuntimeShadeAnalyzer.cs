using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>Runtime-safe road shade analysis using the Collider layers embedded in an inspection Player.</summary>
public static class EnvironmentCostRuntimeShadeAnalyzer
{
    public static EnvironmentCostRuntimeShadeAnalysisResult Analyze(EnvironmentCostRuntimeShadeAnalysisInput input,
        EnvironmentCostRuntimeShadeAnalysisRequest request, Func<bool> isCancellationRequested = null,
        Action<int, int> onEdgeCompleted = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        input.Validate();
        if (request == null) throw new ArgumentNullException(nameof(request));
        request.Validate(input);

        var result = CreateResult(input, request);
        for (var edgeIndex = 0; edgeIndex < input.edges.Length; edgeIndex++)
        {
            if (isCancellationRequested?.Invoke() == true)
            {
                result.status = "cancelled";
                result.message = "Cancellation was requested before all runtime shade edges were calculated.";
                return result;
            }
            result.edges.Add(AnalyzeEdge(input, input.edges[edgeIndex], request));
            onEdgeCompleted?.Invoke(edgeIndex + 1, input.edges.Length);
        }
        return result;
    }

    public static EnvironmentCostRuntimeShadeAnalysisResult CreateResult(EnvironmentCostRuntimeShadeAnalysisInput input,
        EnvironmentCostRuntimeShadeAnalysisRequest request) => new EnvironmentCostRuntimeShadeAnalysisResult
    {
        schemaVersion = "environment-cost-runtime-shade-result-0.1",
        status = "completed",
        areaId = input.areaId,
        generatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        provenance = EnvironmentCostRuntimeShadeAnalysisProvenance.From(input, request),
        edges = new List<EnvironmentCostRuntimeShadeEdgeResult>(input.edges.Length)
    };

    public static EnvironmentCostRuntimeShadeEdgeResult AnalyzeEdge(EnvironmentCostRuntimeShadeAnalysisInput input,
        EnvironmentCostRuntimeShadeInputEdge edge, EnvironmentCostRuntimeShadeAnalysisRequest request)
    {
        var suns = new Dictionary<int, HourlyEnvironmentCostRules.SunPosition>();
        foreach (var hour in request.hours)
            suns[hour] = HourlyEnvironmentCostRules.CalculateSun(request.analysisDate, hour, input.center[1], input.center[0], input.timezone);
        return AnalyzeEdge(input, edge, request, suns, 1 << request.roadLayer, 1 << request.buildingLayer);
    }

    private static EnvironmentCostRuntimeShadeEdgeResult AnalyzeEdge(EnvironmentCostRuntimeShadeAnalysisInput input,
        EnvironmentCostRuntimeShadeInputEdge edge, EnvironmentCostRuntimeShadeAnalysisRequest request,
        IReadOnlyDictionary<int, HourlyEnvironmentCostRules.SunPosition> suns, int roadMask, int buildingMask)
    {
        var from = new Vector3(edge.from[0], 500.0f, edge.from[1]);
        var to = new Vector3(edge.to[0], 500.0f, edge.to[1]);
        var subdivisions = Math.Max(1, (int)Math.Ceiling(edge.lengthMeters / input.sampleSpacingMeters));
        var shadeCounts = new Dictionary<int, int>();
        foreach (var hour in request.hours) shadeCounts[hour] = 0;
        var samples = 0;
        var validSamples = 0;
        var noGroundSamples = 0;
        for (var sampleIndex = 0; sampleIndex <= subdivisions; sampleIndex++)
        {
            var point = Vector3.Lerp(from, to, sampleIndex / (float)subdivisions);
            if (new Vector2(point.x, point.z).magnitude > input.radiusMeters) continue;
            samples++;
            if (!Physics.Raycast(point, Vector3.down, out var groundHit, 1000.0f, roadMask, QueryTriggerInteraction.Ignore))
            {
                noGroundSamples++;
                continue;
            }
            validSamples++;
            var pedestrianPoint = groundHit.point + Vector3.up * input.pedestrianHeightMeters;
            foreach (var hour in request.hours)
            {
                var sun = suns[hour];
                if (sun.elevationDegrees > 0.0 && Physics.Raycast(pedestrianPoint, sun.direction, 10000.0f, buildingMask,
                        QueryTriggerInteraction.Ignore))
                    shadeCounts[hour]++;
            }
        }

        var hourly = new EnvironmentCostRuntimeShadeHourlyResult[request.hours.Length];
        for (var index = 0; index < request.hours.Length; index++)
        {
            var hour = request.hours[index];
            var sun = suns[hour];
            var status = HourlyEnvironmentCostRules.DetermineStatus(samples, validSamples, noGroundSamples,
                sun.elevationDegrees, out var reason);
            var shadeRatio = status == "missing" ? -1.0 : shadeCounts[hour] / (double)validSamples;
            hourly[index] = new EnvironmentCostRuntimeShadeHourlyResult
            {
                hour = hour,
                timestamp = HourlyEnvironmentCostRules.Timestamp(request.analysisDate, hour, input.timezone),
                status = status,
                exclusionReason = reason,
                shadeRatio = shadeRatio,
                solarExposureSeconds = shadeRatio < 0.0 ? -1.0 : HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(edge.walkingSeconds, shadeRatio),
                sampleCount = samples,
                validSampleCount = validSamples,
                noGroundSampleCount = noGroundSamples
            };
        }
        return new EnvironmentCostRuntimeShadeEdgeResult { id = edge.id, hourly = hourly };
    }
}

[Serializable]
public sealed class EnvironmentCostRuntimeShadeAnalysisInput
{
    public string schemaVersion;
    public string areaId;
    public double[] center;
    public int coordinateZoneId;
    public float radiusMeters;
    public string analysisDate;
    public string timezone;
    public float sampleSpacingMeters;
    public float pedestrianHeightMeters;
    // Present for sidewalk-network v2 inputs.  Keeping it nullable allows existing 0.1
    // packages to remain readable while package generation moves to physical geometry.
    public string graphFingerprintSha256;
    public EnvironmentCostRuntimeShadeInputQuality quality;
    public EnvironmentCostRuntimeShadeInputEdge[] edges;

    public void Validate()
    {
        if ((!string.Equals(schemaVersion, "environment-cost-runtime-shade-input-0.1", StringComparison.Ordinal) &&
             !string.Equals(schemaVersion, "environment-cost-runtime-shade-input-0.3", StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(areaId) || center == null || center.Length != 2 || coordinateZoneId < 1 || coordinateZoneId > 19 ||
            !DateTime.TryParseExact(analysisDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            string.IsNullOrWhiteSpace(timezone) || radiusMeters <= 0 || sampleSpacingMeters <= 0 || pedestrianHeightMeters < 0 || edges == null || edges.Length == 0)
            throw new InvalidOperationException("Runtime shade input is incomplete.");
        foreach (var edge in edges) edge.Validate();
        if (string.Equals(schemaVersion, "environment-cost-runtime-shade-input-0.3", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(graphFingerprintSha256) || graphFingerprintSha256.Length != 64 || quality == null)
                throw new InvalidOperationException("Runtime shade input v0.3 is missing sidewalk graph provenance.");
            var physicalIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in edges)
                if (string.IsNullOrWhiteSpace(edge.physicalEdgeId) || !physicalIds.Add(edge.physicalEdgeId))
                    throw new InvalidOperationException("Runtime shade input v0.3 must sample every physical edge exactly once.");
        }
    }
}

[Serializable]
public sealed class EnvironmentCostRuntimeShadeInputEdge
{
    public string id;
    public string physicalEdgeId;
    public float[] from;
    public float[] to;
    public double lengthMeters;
    public double walkingSeconds;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(id) || from == null || from.Length != 2 || to == null || to.Length != 2 ||
            lengthMeters <= 0 || walkingSeconds <= 0) throw new InvalidOperationException("Runtime shade input edge is invalid.");
    }
}

[Serializable]
public sealed class EnvironmentCostRuntimeShadeInputQuality
{
    public string status;
    public double explicitOrDerivedRatio;
    public double fallbackRatio;
    public string sourceSchemaVersion;
}

[Serializable]
public sealed class EnvironmentCostRuntimeShadeAnalysisRequest
{
    public DateTime analysisDate;
    public int[] hours;
    public int buildingLayer = 8;
    public int roadLayer = 9;
    public void Validate(EnvironmentCostRuntimeShadeAnalysisInput input)
    {
        if (hours == null || hours.Length == 0 || buildingLayer < 0 || buildingLayer > 31 || roadLayer < 0 || roadLayer > 31)
            throw new InvalidOperationException("Runtime shade analysis request is invalid.");
        foreach (var hour in hours) if (hour < 0 || hour > 23) throw new InvalidOperationException("Runtime shade analysis hours are invalid.");
        if (string.IsNullOrWhiteSpace(input.timezone)) throw new InvalidOperationException("Runtime shade analysis timezone is missing.");
    }
}

[Serializable]
public sealed class EnvironmentCostRuntimeShadeAnalysisResult
{
    public string schemaVersion;
    public string status;
    public string message;
    public string areaId;
    public string generatedAtUtc;
    public EnvironmentCostRuntimeShadeAnalysisProvenance provenance;
    public List<EnvironmentCostRuntimeShadeEdgeResult> edges;
}

[Serializable] public sealed class EnvironmentCostRuntimeShadeEdgeResult { public string id; public EnvironmentCostRuntimeShadeHourlyResult[] hourly; }
[Serializable] public sealed class EnvironmentCostRuntimeShadeHourlyResult { public int hour; public string timestamp; public string status; public string exclusionReason; public double shadeRatio; public double solarExposureSeconds; public int sampleCount; public int validSampleCount; public int noGroundSampleCount; }

[Serializable]
public sealed class EnvironmentCostRuntimeShadeAnalysisProvenance
{
    public string areaId;
    public int coordinateZoneId;
    public double[] center;
    public float radiusMeters;
    public string analysisDate;
    public string timezone;
    public int[] hours;
    public float sampleSpacingMeters;
    public float pedestrianHeightMeters;
    public int buildingLayer;
    public int roadLayer;
    public string obstructionCondition;
    public string groundCondition;
    public string scenarioId;
    public string policyFingerprintSha256;
    public string cityPackageVersion;
    public string cityPackageManifestSha256;
    public string recalculationScope;
    public int totalEdgeCount;
    public int recalculatedEdgeCount;
    /// <summary>Algorithm used for resultFingerprintSha256.  Explicit so old JSON is never mistaken for a verified result.</summary>
    public string resultFingerprintAlgorithm;
    public string resultFingerprintSha256;
    public string graphFingerprintSha256;
    public EnvironmentCostRuntimeShadeInputQuality networkQuality;
    public static EnvironmentCostRuntimeShadeAnalysisProvenance From(EnvironmentCostRuntimeShadeAnalysisInput input,
        EnvironmentCostRuntimeShadeAnalysisRequest request) => new EnvironmentCostRuntimeShadeAnalysisProvenance
    {
        areaId = input.areaId, coordinateZoneId = input.coordinateZoneId, center = input.center, radiusMeters = input.radiusMeters, analysisDate = request.analysisDate.ToString("yyyy-MM-dd"),
        timezone = input.timezone, hours = request.hours, sampleSpacingMeters = input.sampleSpacingMeters,
        pedestrianHeightMeters = input.pedestrianHeightMeters, buildingLayer = request.buildingLayer, roadLayer = request.roadLayer,
        obstructionCondition = "Physics.Raycast toward solar direction against Building layer", groundCondition = "Physics.Raycast downward against Road layer",
        graphFingerprintSha256 = input.graphFingerprintSha256, networkQuality = input.quality
    };
}
