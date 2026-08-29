using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

[Serializable]
public sealed class AnalysisRunConfig
{
    public string schemaVersion;
    public string areaId;
    public double[] center;
    public double radiusMeters;
    public int coordinateZoneId;
    public string date;
    public string timezone;
    public int[] hours;
    public double sampleSpacingMeters;
    public double pedestrianHeightMeters;
    public double walkingSpeedMetersPerSecond;
    public string[] candidateDatasetIds;
    public Dictionary<string, string> datasetRoots;
    public string osmInputPath;
    public string coverageOutputPath;
    public string environmentCostOutputPath;
    public string summaryOutputPath;
    public string cacheDirectoryPath;
    public string stateOutputPath;
    public string cancellationRequestPath;
    public string policyScenarioInputPath;
    public bool includeCityGmlVegetation;
    public MeshPartitionSettings meshPartition;

    [JsonIgnore] public string repositoryRoot;

    [JsonIgnore] public double CenterLongitude => center[0];
    [JsonIgnore] public double CenterLatitude => center[1];
    [JsonIgnore] public DateTime AnalysisDate => DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static AnalysisRunConfig LoadForCurrentProcess()
    {
        var configArgument = FindCommandLineValue("-analysisConfig");
        if (string.IsNullOrWhiteSpace(configArgument))
        {
            throw new ArgumentException("Pass -analysisConfig <repository-relative-or-absolute-path> to the Unity process.");
        }

        var repositoryRoot = FindRepositoryRoot();
        var configPath = ResolvePath(repositoryRoot, configArgument);
        if (!File.Exists(configPath)) throw new FileNotFoundException("Analysis config was not found.", configPath);

        return LoadFromPath(configPath, repositoryRoot);
    }

    /// <summary>Loads and validates a configuration selected from an Editor window.</summary>
    public static AnalysisRunConfig LoadForEditor(string configPath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var resolvedPath = ResolvePath(repositoryRoot, configPath);
        if (!File.Exists(resolvedPath)) throw new FileNotFoundException("Analysis config was not found.", resolvedPath);
        return LoadFromPath(resolvedPath, repositoryRoot);
    }

    public string ResolvePath(string path) => ResolvePath(repositoryRoot, path);

    [JsonIgnore] public string CacheDirectoryPath => ResolvePath(cacheDirectoryPath);
    [JsonIgnore] public string StateOutputPath => ResolvePath(stateOutputPath);
    [JsonIgnore] public string CancellationRequestPath => ResolvePath(cancellationRequestPath);
    [JsonIgnore] public bool ForceRecalculate => HasCommandLineFlag("-forceRecalculate");
    [JsonIgnore] public string SelectedMeshUnitId => FindCommandLineValue("-meshUnit");

    public string DatasetRootFor(string datasetId)
    {
        if (datasetRoots == null || !datasetRoots.TryGetValue(datasetId, out var path))
        {
            throw new KeyNotFoundException($"datasetRoots does not contain dataset ID {datasetId}.");
        }
        return ResolvePath(path);
    }

    private static AnalysisRunConfig LoadFromPath(string configPath, string repositoryRoot)
    {
        var config = JsonConvert.DeserializeObject<AnalysisRunConfig>(File.ReadAllText(configPath))
            ?? throw new InvalidOperationException("Analysis config could not be parsed.");
        config.repositoryRoot = repositoryRoot;
        config.Validate(configPath);
        return config;
    }

    private void Validate(string configPath)
    {
        if (!string.Equals(schemaVersion, "environment-cost-analysis-config-0.2", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported analysis config schemaVersion: {configPath}");
        if (string.IsNullOrWhiteSpace(areaId)) throw new InvalidOperationException($"areaId is required: {configPath}");
        if (center == null || center.Length != 2) throw new InvalidOperationException($"center must be [longitude, latitude]: {configPath}");
        if (radiusMeters <= 0) throw new InvalidOperationException($"radiusMeters must be positive: {configPath}");
        if (coordinateZoneId < 1 || coordinateZoneId > 19) throw new InvalidOperationException($"coordinateZoneId is invalid: {configPath}");
        if (hours == null || hours.Length == 0 || hours.Any(hour => hour < 0 || hour > 23) ||
            hours.Distinct().Count() != hours.Length || !hours.SequenceEqual(hours.OrderBy(hour => hour)))
        {
            throw new InvalidOperationException($"hours must contain unique ascending local hours: {configPath}");
        }
        if (string.IsNullOrWhiteSpace(timezone)) throw new InvalidOperationException($"timezone is required: {configPath}");
        if (sampleSpacingMeters <= 0 || pedestrianHeightMeters < 0 || walkingSpeedMetersPerSecond <= 0) throw new InvalidOperationException($"sampling settings are invalid: {configPath}");
        _ = AnalysisDate;
        if (string.IsNullOrWhiteSpace(osmInputPath) || string.IsNullOrWhiteSpace(coverageOutputPath) ||
            string.IsNullOrWhiteSpace(environmentCostOutputPath) || string.IsNullOrWhiteSpace(summaryOutputPath) ||
            string.IsNullOrWhiteSpace(cacheDirectoryPath) || string.IsNullOrWhiteSpace(stateOutputPath) ||
            string.IsNullOrWhiteSpace(cancellationRequestPath))
        {
            throw new InvalidOperationException($"input/output paths are required: {configPath}");
        }
        if (meshPartition == null) return;
        if (string.IsNullOrWhiteSpace(meshPartition.planOutputPath) ||
            string.IsNullOrWhiteSpace(meshPartition.unitOutputDirectory) ||
            string.IsNullOrWhiteSpace(meshPartition.unitStateDirectory) ||
            string.IsNullOrWhiteSpace(meshPartition.unitCacheDirectory) ||
            meshPartition.shadowBufferMeters < 0.0)
        {
            throw new InvalidOperationException($"meshPartition settings are invalid: {configPath}");
        }
    }

    private static string FindCommandLineValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }
        return null;
    }

    private static bool HasCommandLineFlag(string name) => Environment.GetCommandLineArgs()
        .Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    private static string FindRepositoryRoot()
    {
        var current = Directory.GetParent(Application.dataPath)?.Parent;
        while (current != null)
        {
            var gitMarker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker)) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root (.git directory) was not found from this Unity project.");
    }

    private static string ResolvePath(string root, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
}

/// <summary>Optional paths and boundary buffer used by mesh-partitioned batch analysis.</summary>
[Serializable]
public sealed class MeshPartitionSettings
{
    public string planOutputPath;
    public string unitOutputDirectory;
    public string unitStateDirectory;
    public string unitCacheDirectory;
    public double shadowBufferMeters;
}
