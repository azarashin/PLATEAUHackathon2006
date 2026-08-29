using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Persisted, player-editable policy scenario.  The original 0.1 policy JSON remains
/// importable; this document additionally records the Runtime package used for editing.
/// </summary>
[Serializable]
public sealed class EnvironmentCostRuntimePolicyScenario
{
    public const string SchemaVersion = "environment-cost-runtime-policy-scenario-0.1";
    public string schemaVersion = SchemaVersion;
    public string id = "runtime-scenario";
    public string displayName = "New scenario";
    public string areaId;
    public int coordinateZoneId;
    public double centerLongitude;
    public double centerLatitude;
    public string cityPackageVersion;
    public string cityPackageManifestSha256;
    public string author;
    public string evidenceMemo;
    public string createdAtUtc;
    public string updatedAtUtc;
    public List<EnvironmentCostRuntimePolicyFacility> facilities = new List<EnvironmentCostRuntimePolicyFacility>();

    public void Validate(string source)
    {
        if (schemaVersion != SchemaVersion) throw new InvalidOperationException($"Unsupported Runtime policy scenario schema: {source}");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException($"Scenario id is required: {source}");
        if (string.IsNullOrWhiteSpace(areaId)) throw new InvalidOperationException($"Scenario area id is required: {source}");
        if (coordinateZoneId < 1 || coordinateZoneId > 19 || !IsFinite(centerLongitude) || !IsFinite(centerLatitude) ||
            centerLatitude < -90.0 || centerLatitude > 90.0 || centerLongitude < -180.0 || centerLongitude > 180.0)
            throw new InvalidOperationException($"Scenario coordinate reference is invalid: {source}");
        facilities ??= new List<EnvironmentCostRuntimePolicyFacility>();
        if (facilities.GroupBy(item => item.id, StringComparer.Ordinal).Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1))
            throw new InvalidOperationException($"Facility ids must be non-empty and unique: {source}");
        foreach (var facility in facilities) facility.Validate(source);
    }

    public string Fingerprint()
    {
        var stable = new
        {
            schemaVersion, id, areaId, coordinateZoneId, centerLongitude, centerLatitude, cityPackageVersion, cityPackageManifestSha256,
            facilities = facilities.OrderBy(item => item.id, StringComparer.Ordinal).Select(item => new
            {
                item.id, item.type, item.localPosition, item.latitude, item.longitude, item.heightMeters,
                item.radiusMeters, item.widthMeters, item.depthMeters, item.rotationDegrees
            })
        };
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(stable, Formatting.None))))
            .Replace("-", string.Empty).ToLowerInvariant();
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

[Serializable]
public sealed class EnvironmentCostRuntimePolicyFacility
{
    public string id;
    public string type;
    public Vector3 localPosition;
    public double latitude;
    public double longitude;
    public float rotationDegrees;
    public double heightMeters = 6.0;
    public double radiusMeters = 1.8;
    public double widthMeters = 4.0;
    public double depthMeters = 4.0;

    public void Validate(string source)
    {
        if (type != "tree" && type != "shade" && type != "obstacle")
            throw new InvalidOperationException($"Unsupported Runtime facility type '{type}': {source}");
        if (heightMeters <= 0.0) throw new InvalidOperationException($"Facility height must be positive: {source}");
        if (!IsFinite(latitude) || !IsFinite(longitude) || latitude < -90.0 || latitude > 90.0 || longitude < -180.0 || longitude > 180.0 ||
            !IsFinite(localPosition.x) || !IsFinite(localPosition.y) || !IsFinite(localPosition.z))
            throw new InvalidOperationException($"Facility coordinates are invalid: {source}");
        if (type == "tree" && radiusMeters <= 0.0) throw new InvalidOperationException($"Tree radius must be positive: {source}");
        if ((type == "shade" || type == "obstacle") && (widthMeters <= 0.0 || depthMeters <= 0.0))
            throw new InvalidOperationException($"Facility dimensions must be positive: {source}");
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

/// <summary>Atomic store for Runtime scenarios. StreamingAssets is deliberately never written.</summary>
public static class EnvironmentCostRuntimePolicyScenarioStore
{
    public static string GetDirectory(string areaId) => Path.Combine(Application.persistentDataPath, "EnvironmentCostScenarios", areaId);
    public static string GetPath(string areaId, string id) => Path.Combine(GetDirectory(areaId), SanitizeFileName(id) + ".json");

    public static void Save(EnvironmentCostRuntimePolicyScenario scenario)
    {
        scenario.updatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(scenario.createdAtUtc)) scenario.createdAtUtc = scenario.updatedAtUtc;
        scenario.Validate(scenario.id);
        var path = GetPath(scenario.areaId, scenario.id);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + ".partial";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(scenario, Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
    }

    public static EnvironmentCostRuntimePolicyScenario Load(string path)
    {
        var scenario = JsonConvert.DeserializeObject<EnvironmentCostRuntimePolicyScenario>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Runtime policy scenario could not be parsed.");
        scenario.Validate(path);
        return scenario;
    }

    public static string[] List(string areaId)
    {
        var directory = GetDirectory(areaId);
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
