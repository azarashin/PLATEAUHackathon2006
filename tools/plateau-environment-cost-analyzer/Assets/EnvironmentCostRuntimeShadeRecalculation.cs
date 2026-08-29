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
    public static string GetDirectory(string areaId, string scenarioId) => Path.Combine(Application.persistentDataPath,
        "EnvironmentCostAnalysis", SanitizeFileName(areaId), SanitizeFileName(scenarioId));

    public static string Save(EnvironmentCostRuntimeShadeAnalysisResult result, string scenarioId)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        var directory = GetDirectory(result.areaId, scenarioId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "latest.json");
        var temporary = path + ".partial";
        var json = JsonUtility.ToJson(result, true);
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        return path;
    }

    public static string CalculateSha256(EnvironmentCostRuntimeShadeAnalysisResult result)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(result))))
            .Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string((value ?? "unknown").Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
