using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

[Serializable]
public sealed class EnvironmentCostPolicyScenario
{
    public string schemaVersion = "environment-cost-policy-scenario-0.1";
    public string id = "baseline";
    public string recalculationScope = "all";
    public List<EnvironmentCostPolicyFacility> facilities = new List<EnvironmentCostPolicyFacility>();

    public static EnvironmentCostPolicyScenario Load(AnalysisRunConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.policyScenarioInputPath)) return new EnvironmentCostPolicyScenario();
        var path = config.ResolvePath(config.policyScenarioInputPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Policy scenario was not found.", path);
        var scenario = JsonConvert.DeserializeObject<EnvironmentCostPolicyScenario>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Policy scenario could not be parsed.");
        scenario.Validate(path);
        return scenario;
    }

    public void Validate(string source)
    {
        if (!string.Equals(schemaVersion, "environment-cost-policy-scenario-0.1", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported policy scenario schemaVersion: {source}");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException($"Policy scenario id is required: {source}");
        if (recalculationScope != "all")
            throw new InvalidOperationException($"Only full scenario recalculation is currently supported: {source}");
        facilities ??= new List<EnvironmentCostPolicyFacility>();
        if (facilities.GroupBy(facility => facility.id, StringComparer.Ordinal).Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1))
            throw new InvalidOperationException($"Policy facility ids must be non-empty and unique: {source}");
        foreach (var facility in facilities) facility.Validate(source);
    }

    public string Fingerprint()
    {
        var stable = new
        {
            schemaVersion,
            id,
            recalculationScope,
            facilities = (facilities ?? new List<EnvironmentCostPolicyFacility>()).OrderBy(facility => facility.id, StringComparer.Ordinal)
                .Select(facility => new { facility.id, facility.type, facility.latitude, facility.longitude, facility.heightMeters, facility.radiusMeters, facility.widthMeters, facility.depthMeters })
        };
        var json = JsonConvert.SerializeObject(stable, Formatting.None);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", string.Empty).ToLowerInvariant();
    }

    public void Save(string path)
    {
        Validate(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Scenario output directory is missing."));
        var temporary = path + ".partial";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
}

[Serializable]
public sealed class EnvironmentCostPolicyFacility
{
    public string id;
    public string type;
    public double latitude;
    public double longitude;
    public double heightMeters = 6.0;
    // Typical street-tree canopy radius. The displayed/occluding canopy is an ellipsoid, not a sphere.
    public double radiusMeters = 1.8;
    public double widthMeters = 4.0;
    public double depthMeters = 4.0;

    public void Validate(string source)
    {
        if (type != "tree" && type != "shade") throw new InvalidOperationException($"Unsupported policy facility type '{type}': {source}");
        if (latitude < -90.0 || latitude > 90.0 || longitude < -180.0 || longitude > 180.0 || heightMeters <= 0.0)
            throw new InvalidOperationException($"Policy facility coordinates or height are invalid: {source}");
        if (type == "tree" && radiusMeters <= 0.0) throw new InvalidOperationException($"Tree radius must be positive: {source}");
        if (type == "shade" && (widthMeters <= 0.0 || depthMeters <= 0.0)) throw new InvalidOperationException($"Shade dimensions must be positive: {source}");
    }
}
