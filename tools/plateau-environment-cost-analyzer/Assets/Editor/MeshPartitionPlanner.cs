using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PLATEAU.Dataset;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Makes stable, third-mesh-sized work units from a coverage report.  A unit owns
/// only its core rectangle; nearby source meshes are imported as a halo so that a
/// building just outside the core can still cast a shadow into it.
/// </summary>
public static class MeshPartitionPlanner
{
    public static void Run()
    {
        try
        {
            var config = AnalysisRunConfig.LoadForCurrentProcess();
            var plan = CreatePlan(config);
            WriteJsonAtomic(config.ResolvePath(config.meshPartition.planOutputPath), plan);
            Debug.Log($"ENVIRONMENT_COST_MESH_PLAN_COMPLETE area={config.areaId} units={plan.units.Count} output={config.ResolvePath(config.meshPartition.planOutputPath)}");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    internal static MeshPartitionUnit LoadSelectedUnit(AnalysisRunConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SelectedMeshUnitId)) return null;
        if (config.meshPartition == null) throw new InvalidOperationException("meshPartition is required with -meshUnit.");
        var planPath = config.ResolvePath(config.meshPartition.planOutputPath);
        if (!File.Exists(planPath)) throw new FileNotFoundException("Create the mesh partition plan before running a unit.", planPath);
        var plan = JsonConvert.DeserializeObject<MeshPartitionPlan>(File.ReadAllText(planPath));
        if (plan == null || plan.schemaVersion != "environment-cost-mesh-partition-plan-0.1" || plan.areaId != config.areaId)
            throw new InvalidOperationException($"Invalid mesh partition plan: {planPath}");
        var unit = plan.units?.SingleOrDefault(value => value.id == config.SelectedMeshUnitId);
        if (unit == null) throw new InvalidOperationException($"Mesh unit was not found in the plan: {config.SelectedMeshUnitId}");
        return unit;
    }

    internal static MeshPartitionPlan CreatePlan(AnalysisRunConfig config)
    {
        if (config.meshPartition == null) throw new InvalidOperationException("meshPartition is required to create a mesh plan.");
        var coveragePath = config.ResolvePath(config.coverageOutputPath);
        var coverage = JsonConvert.DeserializeObject<CoverageReport>(File.ReadAllText(coveragePath))
            ?? throw new InvalidOperationException("Coverage report could not be parsed.");
        if (!string.IsNullOrWhiteSpace(coverage.areaId) && coverage.areaId != config.areaId)
            throw new InvalidOperationException("Coverage areaId does not match analysis config.");

        var sourceGrids = coverage.datasets.SelectMany(dataset => (dataset.gridCodes ?? new List<string>())
            .Where(MeshCoverageAnalyzer.IsSupportedGridCode)
            .Select(code => new SourceGrid { datasetId = dataset.id, code = code })).ToList();
        var coreCodes = sourceGrids.SelectMany(item => ExpandToThirdMeshes(item.code)).Distinct(StringComparer.Ordinal)
            .Where(code => IntersectsAnalysisCircle(code, config)).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        if (coreCodes.Length == 0) throw new InvalidOperationException("Coverage report contains no supported mesh codes.");

        var plan = new MeshPartitionPlan
        {
            schemaVersion = "environment-cost-mesh-partition-plan-0.1",
            areaId = config.areaId,
            generatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            coveragePath = config.coverageOutputPath,
            shadowBufferMeters = config.meshPartition.shadowBufferMeters,
            ownershipRule = "latitude/longitude minimum-inclusive, maximum-exclusive; each sample belongs to exactly one canonical third mesh",
            units = new List<MeshPartitionUnit>()
        };
        foreach (var coreCode in coreCodes)
        {
            using var grid = GridCode.Create(coreCode);
            var extent = grid.Extent;
            var halo = ExpandExtent(extent.Min.Latitude, extent.Min.Longitude, extent.Max.Latitude, extent.Max.Longitude,
                config.meshPartition.shadowBufferMeters, config.CenterLatitude);
            var matching = sourceGrids.Where(source => Intersects(source.code, halo)).GroupBy(source => source.datasetId)
                .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new MeshPartitionDataset
                {
                    id = group.Key,
                    gridCodes = group.Select(value => value.code).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()
                }).ToList();
            plan.units.Add(new MeshPartitionUnit
            {
                id = $"mesh-{coreCode}", coreGridCode = coreCode,
                minLatitude = extent.Min.Latitude, minLongitude = extent.Min.Longitude,
                maxLatitude = extent.Max.Latitude, maxLongitude = extent.Max.Longitude,
                datasets = matching,
                outputPath = Path.Combine(config.meshPartition.unitOutputDirectory, $"mesh-{coreCode}.json").Replace('\\', '/'),
                statePath = Path.Combine(config.meshPartition.unitStateDirectory, $"mesh-{coreCode}.json").Replace('\\', '/'),
                cacheDirectoryPath = Path.Combine(config.meshPartition.unitCacheDirectory, $"mesh-{coreCode}").Replace('\\', '/')
            });
        }
        return plan;
    }

    internal static IEnumerable<string> ExpandToThirdMeshes(string code)
    {
        if (code?.Length == 8) return new[] { code };
        if (code?.Length == 6) return new[] { code + "00", code + "01", code + "10", code + "11" };
        return Array.Empty<string>();
    }

    internal static bool Owns(MeshPartitionUnit unit, double latitude, double longitude) =>
        latitude >= unit.minLatitude && latitude < unit.maxLatitude && longitude >= unit.minLongitude && longitude < unit.maxLongitude;

    private static bool Intersects(string sourceCode, (double minLat, double minLon, double maxLat, double maxLon) extent)
    {
        using var grid = GridCode.Create(sourceCode);
        var source = grid.Extent;
        return source.Min.Latitude < extent.maxLat && source.Max.Latitude > extent.minLat &&
            source.Min.Longitude < extent.maxLon && source.Max.Longitude > extent.minLon;
    }

    private static bool IntersectsAnalysisCircle(string code, AnalysisRunConfig config)
    {
        using var grid = GridCode.Create(code);
        var extent = grid.Extent;
        var latitude = Math.Max(extent.Min.Latitude, Math.Min(config.CenterLatitude, extent.Max.Latitude));
        var longitude = Math.Max(extent.Min.Longitude, Math.Min(config.CenterLongitude, extent.Max.Longitude));
        return DistanceMeters(config.CenterLatitude, config.CenterLongitude, latitude, longitude) <= config.radiusMeters;
    }

    private static double DistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double earthRadiusMeters = 6371008.8;
        var latitudeDelta = (latitudeB - latitudeA) * Math.PI / 180.0;
        var longitudeDelta = (longitudeB - longitudeA) * Math.PI / 180.0;
        var sinLatitude = Math.Sin(latitudeDelta / 2.0);
        var sinLongitude = Math.Sin(longitudeDelta / 2.0);
        var haversine = sinLatitude * sinLatitude + Math.Cos(latitudeA * Math.PI / 180.0) *
            Math.Cos(latitudeB * Math.PI / 180.0) * sinLongitude * sinLongitude;
        return earthRadiusMeters * 2.0 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1.0 - haversine));
    }

    private static (double minLat, double minLon, double maxLat, double maxLon) ExpandExtent(double minLat, double minLon,
        double maxLat, double maxLon, double meters, double referenceLatitude)
    {
        const double metersPerLatitudeDegree = 111320.0;
        var latitudeDelta = meters / metersPerLatitudeDegree;
        var longitudeDelta = meters / Math.Max(1.0, metersPerLatitudeDegree * Math.Cos(referenceLatitude * Math.PI / 180.0));
        return (minLat - latitudeDelta, minLon - longitudeDelta, maxLat + latitudeDelta, maxLon + longitudeDelta);
    }

    private static void WriteJsonAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        var temporary = path + ".partial";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    [Serializable] private sealed class CoverageReport { public string areaId; public List<CoverageDataset> datasets; }
    [Serializable] private sealed class CoverageDataset { public string id; public List<string> gridCodes; }
    [Serializable] private sealed class SourceGrid { public string datasetId; public string code; }
}

[Serializable]
public sealed class MeshPartitionPlan
{
    public string schemaVersion;
    public string areaId;
    public string generatedAt;
    public string coveragePath;
    public double shadowBufferMeters;
    public string ownershipRule;
    public List<MeshPartitionUnit> units;
}

[Serializable]
public sealed class MeshPartitionUnit
{
    public string id;
    public string coreGridCode;
    public double minLatitude;
    public double minLongitude;
    public double maxLatitude;
    public double maxLongitude;
    public List<MeshPartitionDataset> datasets;
    public string outputPath;
    public string statePath;
    public string cacheDirectoryPath;
}

[Serializable]
public sealed class MeshPartitionDataset { public string id; public string[] gridCodes; }
