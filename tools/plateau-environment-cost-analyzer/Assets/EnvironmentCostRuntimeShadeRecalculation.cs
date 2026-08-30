using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>Chooses a conservative set of road edges that may be affected by an edited policy facility.</summary>
public static class EnvironmentCostRuntimePolicyImpact
{
    public static HashSet<string> FindAffectedEdgeIds(EnvironmentCostRuntimeShadeAnalysisInput input,
        EnvironmentCostRuntimeShadeAnalysisRequest request, IEnumerable<EnvironmentCostRuntimePolicyFacility> changedFacilities)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (request == null) throw new ArgumentNullException(nameof(request));
        var facilities = (changedFacilities ?? Array.Empty<EnvironmentCostRuntimePolicyFacility>()).Where(item => item != null).ToArray();
        if (facilities.Length == 0) return new HashSet<string>(input.edges.Select(edge => edge.id), StringComparer.Ordinal);

        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hour in request.hours)
        {
            var sun = HourlyEnvironmentCostRules.CalculateSun(request.analysisDate, hour, input.center[1], input.center[0], input.timezone);
            if (sun.elevationDegrees <= 0.0) continue;
            var tangent = Mathf.Max(0.01f, Mathf.Tan((float)(sun.elevationDegrees * Math.PI / 180.0)));
            foreach (var edge in input.edges)
            {
                var from = new Vector2(edge.from[0], edge.from[1]);
                var to = new Vector2(edge.to[0], edge.to[1]);
                // Runtime analysis samples only the part of an edge inside the source analysis
                // extent. A wholly excluded edge is not output coverage.
                if (DistanceToSegment(Vector2.zero, from, to) > input.radiusMeters) continue;
                foreach (var facility in facilities)
                {
                    var footprint = facility.type == "tree" ? (float)facility.radiusMeters : Mathf.Max((float)facility.widthMeters, (float)facility.depthMeters) * .5f;
                    var shadowReach = footprint + (float)facility.heightMeters / tangent + input.sampleSpacingMeters;
                    if (DistanceToSegment(new Vector2(facility.localPosition.x, facility.localPosition.z), from, to) <= shadowReach)
                    {
                        affected.Add(edge.id);
                        break;
                    }
                }
            }
        }
        return affected;
    }

    /// <summary>
    /// Tests the generated Runtime road-edge inventory rather than a fixed circular authoring
    /// boundary. All daylight hours are considered so a valid placement is not tied to the
    /// currently selected analysis hour.
    /// </summary>
    public static bool HasPotentiallyAffectedEdge(EnvironmentCostRuntimeShadeAnalysisInput input, DateTime analysisDate,
        EnvironmentCostRuntimePolicyFacility facility)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (facility == null) throw new ArgumentNullException(nameof(facility));
        return FindAffectedEdgeIds(input, new EnvironmentCostRuntimeShadeAnalysisRequest
        {
            analysisDate = analysisDate,
            hours = Enumerable.Range(0, 24).ToArray()
        }, new[] { facility }).Count > 0;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 from, Vector2 to)
    {
        var direction = to - from;
        var lengthSquared = direction.sqrMagnitude;
        if (lengthSquared <= 0.000001f) return Vector2.Distance(point, from);
        var t = Mathf.Clamp01(Vector2.Dot(point - from, direction) / lengthSquared);
        return Vector2.Distance(point, from + direction * t);
    }
}

/// <summary>Writes a machine-readable Runtime calculation record outside StreamingAssets.</summary>
public static class EnvironmentCostRuntimeShadeResultStore
{
    public const string SemanticFingerprintAlgorithm = "environment-cost-runtime-shade-semantic-v1";
    public const long MaximumComparableResultBytes = 256L * 1024L * 1024L;
    public static string GetDirectory(string areaId, string scenarioId) => Path.Combine(Application.persistentDataPath,
        "EnvironmentCostAnalysis", SanitizeFileName(areaId), SanitizeFileName(scenarioId));

    public static string Save(EnvironmentCostRuntimeShadeAnalysisResult result, string scenarioId)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        if (result.provenance == null) throw new ArgumentException("Runtime shade result provenance is required.", nameof(result));
        result.provenance.resultFingerprintAlgorithm = SemanticFingerprintAlgorithm;
        result.provenance.resultFingerprintSha256 = CalculateSha256(result);
        var directory = GetDirectory(result.areaId, scenarioId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "latest.json");
        var temporary = path + ".partial";
        // Newtonsoft preserves the double precision used by hourly results across a save/load
        // round trip. JsonUtility can reconstruct a different IEEE-754 value for some decimals,
        // which invalidates the semantic fingerprint even though the file itself is unchanged.
        var json = EnvironmentCostRuntimePolicyJson.Serialize(result, Newtonsoft.Json.Formatting.Indented);
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        return path;
    }

    /// <summary>Loads an analysis result that the route-comparison UI can safely hold in memory.</summary>
    public static EnvironmentCostRuntimeShadeAnalysisResult LoadForRouteComparison(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("解析結果ファイルが見つかりません。", path);
        var bytes = new FileInfo(path).Length;
        if (bytes > MaximumComparableResultBytes)
            throw new InvalidOperationException($"全時刻解析結果は比較画面で読み込める上限（{MaximumComparableResultBytes / 1024 / 1024} MB）を超えています。比較対象時刻を選択し、その時刻だけ再解析してから比較してください: {path}");
        return EnvironmentCostRuntimePolicyJson.Deserialize<EnvironmentCostRuntimeShadeAnalysisResult>(File.ReadAllText(path));
    }

    public static string CalculateSha256(EnvironmentCostRuntimeShadeAnalysisResult result)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        if (result.provenance == null) throw new ArgumentException("Runtime shade result provenance is required.", nameof(result));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer = new FingerprintWriter(hash);
        writer.Text(SemanticFingerprintAlgorithm);
        writer.Text(result.schemaVersion); writer.Text(result.status); writer.Text(result.message); writer.Text(result.areaId); writer.Text(result.generatedAtUtc);
        var provenance = result.provenance;
        writer.Text(provenance.areaId); writer.Int32(provenance.coordinateZoneId); writer.DoubleArray(provenance.center); writer.Single(provenance.radiusMeters);
        writer.Text(provenance.analysisDate); writer.Text(provenance.timezone); writer.Int32Array(provenance.hours); writer.Single(provenance.sampleSpacingMeters); writer.Single(provenance.pedestrianHeightMeters);
        writer.Int32(provenance.buildingLayer); writer.Int32(provenance.roadLayer); writer.Text(provenance.obstructionCondition); writer.Text(provenance.groundCondition);
        writer.Text(provenance.scenarioId); writer.Text(provenance.policyFingerprintSha256); writer.Text(provenance.cityPackageVersion); writer.Text(provenance.cityPackageManifestSha256);
        writer.Text(provenance.recalculationScope); writer.Int32(provenance.totalEdgeCount); writer.Int32(provenance.recalculatedEdgeCount);
        var edges = (result.edges ?? new List<EnvironmentCostRuntimeShadeEdgeResult>()).Where(item => item != null).OrderBy(item => item.id, StringComparer.Ordinal).ToArray();
        writer.Int32(edges.Length);
        foreach (var edge in edges)
        {
            writer.Text(edge.id);
            var hourlyResults = (edge.hourly ?? Array.Empty<EnvironmentCostRuntimeShadeHourlyResult>()).Where(item => item != null).OrderBy(item => item.hour).ToArray();
            writer.Int32(hourlyResults.Length);
            foreach (var hourly in hourlyResults)
            {
                writer.Int32(hourly.hour); writer.Text(hourly.timestamp); writer.Text(hourly.status); writer.Text(hourly.exclusionReason);
                writer.Double(hourly.shadeRatio); writer.Double(hourly.solarExposureSeconds); writer.Int32(hourly.sampleCount); writer.Int32(hourly.validSampleCount); writer.Int32(hourly.noGroundSampleCount);
            }
        }
        return BitConverter.ToString(hash.GetHashAndReset()).Replace("-", string.Empty).ToLowerInvariant();
    }

    private sealed class FingerprintWriter
    {
        private readonly IncrementalHash hash;
        public FingerprintWriter(IncrementalHash hash) => this.hash = hash;
        public void Text(string value)
        {
            // JsonUtility normalizes a null string to an empty string on its JSON round trip.
            // The semantic fingerprint must use that persisted representation as well.
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty); Int32(bytes.Length); hash.AppendData(bytes);
        }
        public void Int32(int value) => hash.AppendData(BitConverter.GetBytes(value));
        public void Single(float value) => hash.AppendData(BitConverter.GetBytes(value));
        public void Double(double value) => hash.AppendData(BitConverter.GetBytes(value));
        public void DoubleArray(double[] values) { Int32(values?.Length ?? -1); if (values != null) foreach (var value in values) Double(value); }
        public void Int32Array(int[] values) { Int32(values?.Length ?? -1); if (values != null) foreach (var value in values) Int32(value); }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string((value ?? "unknown").Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "." || sanitized == "..")
            throw new ArgumentException("解析結果の保存先IDが無効です。", nameof(value));
        return sanitized;
    }
}
