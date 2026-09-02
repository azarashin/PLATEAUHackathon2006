using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PLATEAU.Geometries;
using PLATEAU.Native;
using UnityEditor;
using UnityEngine;

/// <summary>Creates the versioned StreamingAssets package consumed by the standalone Runtime player.</summary>
public static class EnvironmentCostRuntimeCityPackageBuilder
{
    private const string PackageSchema = "environment-cost-runtime-city-package-0.1";

    [MenuItem("PLATEAU/環境コスト/Runtime 都市データパッケージを作成")]
    public static void CreateIchigayaPackageFromMenu()
    {
        Create(RuntimeCityPackageConfig.Load("data/runtime-city-packages/ichigaya-venue.json"));
    }

    /// <summary>Batch entry point: -runtimeCityPackageConfig data/runtime-city-packages/ichigaya-venue.json.</summary>
    public static void Run()
    {
        try
        {
            var value = FindCommandLineValue("-runtimeCityPackageConfig");
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Pass -runtimeCityPackageConfig <path> to Unity.");
            Create(RuntimeCityPackageConfig.Load(value));
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            Debug.LogError("ENVIRONMENT_COST_RUNTIME_CITY_PACKAGE_FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(1);
            else throw;
        }
    }

    public static void Create(RuntimeCityPackageConfig config)
    {
        var analysis = AnalysisRunConfig.LoadForEditor(config.analysisConfigPath);
        if (!string.Equals(analysis.areaId, config.areaId, StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime package areaId must match the analysis config.");
        var roadManifestPath = config.ResolveOptionalPath(config.roadNetworkBundlePath);
        var baselinePath = config.ResolveOptionalPath(config.baselineEnvironmentCostPath);
        var sidewalkPath = config.ResolveOptionalPath(config.sidewalkNetworkPath);
        if (roadManifestPath != null && !File.Exists(roadManifestPath)) throw new FileNotFoundException("Road network bundle manifest was not found.", roadManifestPath);
        if (baselinePath != null && !File.Exists(baselinePath)) throw new FileNotFoundException("Baseline environment cost was not found.", baselinePath);
        if (sidewalkPath != null && !File.Exists(sidewalkPath)) throw new FileNotFoundException("Sidewalk network was not found.", sidewalkPath);

        var targetRoot = Path.Combine(Application.streamingAssetsPath, config.packageRelativePath);
        var stagingRoot = targetRoot + ".staging";
        if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var files = new List<EnvironmentCostRuntimeCityPackageFile>();
            if (baselinePath != null)
                CopyToPackage(baselinePath, stagingRoot, "baseline-environment-cost.json", "baseline-environment-cost", files);
            CreateRuntimeShadeInput(baselinePath, sidewalkPath, analysis, stagingRoot, files);
            if (roadManifestPath != null)
            {
                var roadDirectory = Path.GetDirectoryName(roadManifestPath) ?? throw new InvalidOperationException("Road bundle directory is missing.");
                var roadManifest = JObject.Parse(File.ReadAllText(roadManifestPath));
                CopyToPackage(roadManifestPath, stagingRoot, "road-network/manifest.json", "road-network-manifest", files);
                CopyReferencedRoadFile(roadManifest, "topology.file", "topology", roadDirectory, stagingRoot, files);
                foreach (var slice in roadManifest["costSlices"] as JArray ?? new JArray())
                {
                    var name = (string)slice["file"];
                    if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Road bundle cost slice has no file.");
                    CopyToPackage(Path.Combine(roadDirectory, name), stagingRoot, $"road-network/{name}", "road-network-cost", files);
                }
            }

            var manifest = new EnvironmentCostRuntimeCityPackageManifest
            {
                schemaVersion = PackageSchema,
                areaId = config.areaId,
                displayName = config.displayName,
                version = config.version,
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                coordinateZoneId = analysis.coordinateZoneId,
                center = analysis.center,
                radiusMeters = analysis.radiusMeters,
                inspectionSceneAssetPath = string.IsNullOrWhiteSpace(config.inspectionSceneAssetPath)
                    ? $"Assets/Scenes/EnvironmentCostInspection/{config.areaId}.unity" : config.inspectionSceneAssetPath,
                scene = new EnvironmentCostRuntimeCityPackageScene
                {
                    requiredLayers = new[]
                    {
                        new EnvironmentCostRuntimeCityPackageLayer { name = "Building", layer = 8, role = "display-and-raycast-obstacle" },
                        new EnvironmentCostRuntimeCityPackageLayer { name = "Road", layer = 9, role = "display-and-walkable-surface" },
                        new EnvironmentCostRuntimeCityPackageLayer { name = "Terrain", layer = 10, role = "display-and-terrain-surface" }
                    }
                },
                sources = BuildSources(config, roadManifestPath, baselinePath, sidewalkPath),
                files = files.ToArray()
            };
            manifest.ValidateStructure();
            File.WriteAllText(Path.Combine(stagingRoot, "manifest.json"), JsonUtility.ToJson(manifest, true));
            Verify(stagingRoot);

            Directory.CreateDirectory(Path.GetDirectoryName(targetRoot) ?? throw new InvalidOperationException("StreamingAssets root is missing."));
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, true);
            Directory.Move(stagingRoot, targetRoot);
            AssetDatabase.Refresh();
            Debug.Log($"ENVIRONMENT_COST_RUNTIME_CITY_PACKAGE_READY area={manifest.areaId} version={manifest.version} files={manifest.files.Length} path={targetRoot}");
        }
        catch
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
            throw;
        }
    }

    /// <summary>Verifies a package after generation, or independently from an Editor test/menu command.</summary>
    public static void Verify(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "manifest.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Runtime city package manifest was not found.", path);
        var manifest = JsonUtility.FromJson<EnvironmentCostRuntimeCityPackageManifest>(File.ReadAllText(path));
        if (manifest == null) throw new InvalidOperationException("Runtime city package manifest could not be parsed.");
        manifest.ValidateStructure();
        foreach (var file in manifest.files)
        {
            var fullPath = Path.Combine(packageRoot, file.relativePath);
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length != file.bytes ||
                !string.Equals(EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(fullPath), file.sha256, StringComparison.Ordinal))
                throw new InvalidOperationException($"Runtime city package verification failed: {file.relativePath}");
        }
    }

    private static void CopyReferencedRoadFile(JObject manifest, string tokenPath, string kind, string sourceRoot, string targetRoot,
        List<EnvironmentCostRuntimeCityPackageFile> files)
    {
        var name = (string)manifest.SelectToken(tokenPath);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException($"Road bundle manifest is missing {tokenPath}.");
        CopyToPackage(Path.Combine(sourceRoot, name), targetRoot, $"road-network/{name}", $"road-network-{kind}", files);
    }

    private static void CreateRuntimeShadeInput(string baselinePath, string sidewalkNetworkPath, AnalysisRunConfig analysis, string targetRoot,
        List<EnvironmentCostRuntimeCityPackageFile> files)
    {
        // A v2 physical graph is self-contained for Runtime shade sampling.  Do not parse an
        // optional legacy baseline in that mode: it may describe a different edge inventory.
        var source = string.IsNullOrWhiteSpace(sidewalkNetworkPath) ? JObject.Parse(File.ReadAllText(baselinePath)) : null;
        if (source != null) ValidateBaselineForRuntimeInput(source, analysis);
        var sourceEdges = source?["edges"] as JArray;
        using var worldReference = GeoReference.Create(new PlateauVector3d(0.0, 0.0, 0.0), 1.0f, CoordinateSystem.EUN, analysis.coordinateZoneId);
        var referencePoint = worldReference.Project(new GeoCoordinate(analysis.CenterLatitude, analysis.CenterLongitude, 0.0));
        using var localReference = GeoReference.Create(referencePoint, 1.0f, CoordinateSystem.EUN, analysis.coordinateZoneId);
        var edges = new List<EnvironmentCostRuntimeShadeInputEdge>(sourceEdges?.Count ?? 0);
        string graphFingerprint = null;
        EnvironmentCostRuntimeShadeInputQuality quality = null;
        if (!string.IsNullOrWhiteSpace(sidewalkNetworkPath))
        {
            var path = sidewalkNetworkPath;
            var graph = JObject.Parse(File.ReadAllText(path));
            if (!string.Equals((string)graph["schemaVersion"], "environment-cost-pedestrian-network-2.0", StringComparison.Ordinal) ||
                !string.Equals((string)graph["areaId"], analysis.areaId, StringComparison.Ordinal))
                throw new InvalidOperationException("Sidewalk network must be a matching pedestrian-network v2 document.");
            graphFingerprint = (string)graph["graphFingerprintSha256"];
            if (string.IsNullOrWhiteSpace(graphFingerprint) || graphFingerprint.Length != 64)
                throw new InvalidOperationException("Sidewalk network graph fingerprint is invalid.");
            var graphQuality = graph["quality"] as JObject ?? throw new InvalidOperationException("Sidewalk network has no quality summary.");
            var qualityStatus = (string)graphQuality["status"];
            var fallbackRatio = (double?)graphQuality["fallbackRatio"];
            var supportedRatio = (double?)graphQuality["explicitOrDerivedRatio"];
            var validationFailures = graphQuality["validationFailures"] as JArray;
            var validationWarnings = graphQuality["validationWarnings"] as JArray;
            if (!string.Equals(qualityStatus, "accepted", StringComparison.Ordinal) || !fallbackRatio.HasValue || !supportedRatio.HasValue ||
                fallbackRatio.Value < 0.0 || fallbackRatio.Value > 1.0 || supportedRatio.Value < 0.0 || supportedRatio.Value > 1.0 ||
                Math.Abs(fallbackRatio.Value + supportedRatio.Value - 1.0) > 0.000001 ||
                !string.Equals((string)graphQuality["qualityContractVersion"], "pedestrian-network-safety-1.0", StringComparison.Ordinal) ||
                !string.Equals((string)graphQuality["sourceSchemaVersion"], "0.2", StringComparison.Ordinal) ||
                validationFailures == null || validationFailures.Count != 0 || validationWarnings == null)
                throw new InvalidOperationException("Sidewalk network quality is not accepted for Runtime package generation.");
            var physicalEdges = graph["physicalEdges"] as JArray ?? throw new InvalidOperationException("Sidewalk network has no physicalEdges.");
            foreach (var physical in physicalEdges.OfType<JObject>())
            {
                var geometry = physical["geometry"] as JArray;
                if (geometry == null || geometry.Count < 2) throw new InvalidOperationException("Physical sidewalk edge has invalid geometry.");
                var id = (string)physical["id"] ?? throw new InvalidOperationException("Physical sidewalk edge has no id.");
                edges.Add(new EnvironmentCostRuntimeShadeInputEdge { id = id, physicalEdgeId = id,
                    from = ToLocalPoint(localReference, geometry[0] as JArray), to = ToLocalPoint(localReference, geometry[geometry.Count - 1] as JArray),
                    geometry = geometry.Select(point => ToLocalPoint(localReference, point as JArray)).ToArray(),
                    lengthMeters = (double?)physical["lengthMeters"] ?? throw new InvalidOperationException("Physical sidewalk edge has no length."),
                    walkingSeconds = (double?)physical["walkingSeconds"] ?? throw new InvalidOperationException("Physical sidewalk edge has no walking time.") });
            }
            quality = new EnvironmentCostRuntimeShadeInputQuality { qualityContractVersion = (string)graphQuality["qualityContractVersion"], status = qualityStatus,
                explicitOrDerivedRatio = supportedRatio.Value, fallbackRatio = fallbackRatio.Value, sourceSchemaVersion = (string)graphQuality["sourceSchemaVersion"],
                validationFailures = validationFailures.Select(token => (string)token).ToArray(), validationWarnings = validationWarnings.Select(token => (string)token).ToArray() };
            CopyToPackage(path, targetRoot, "sidewalk-network.json", "sidewalk-network-v2", files);
        }
        else foreach (var sourceEdge in sourceEdges?.OfType<JObject>() ?? throw new InvalidOperationException("Baseline environment cost has no edges."))
        {
            var coordinates = sourceEdge["coordinates"] as JArray;
            if (coordinates == null || coordinates.Count != 2) throw new InvalidOperationException("Baseline edge has invalid coordinates.");
            var id = (string)sourceEdge["id"] ?? throw new InvalidOperationException("Baseline edge has no id.");
            edges.Add(new EnvironmentCostRuntimeShadeInputEdge { id = id, physicalEdgeId = id,
                from = ToLocalPoint(localReference, coordinates[0] as JArray), to = ToLocalPoint(localReference, coordinates[1] as JArray),
                lengthMeters = (double?)sourceEdge["lengthMeters"] ?? throw new InvalidOperationException("Baseline edge has no length."),
                walkingSeconds = (double?)sourceEdge["walkingSeconds"] ?? throw new InvalidOperationException("Baseline edge has no walking time.") });
        }
        var input = new EnvironmentCostRuntimeShadeAnalysisInput
        {
            schemaVersion = quality == null ? "environment-cost-runtime-shade-input-0.1" : "environment-cost-runtime-shade-input-0.3", areaId = analysis.areaId, center = analysis.center,
            coordinateZoneId = analysis.coordinateZoneId, radiusMeters = (float)analysis.radiusMeters, analysisDate = analysis.date, timezone = analysis.timezone,
            sampleSpacingMeters = (float)analysis.sampleSpacingMeters, pedestrianHeightMeters = (float)analysis.pedestrianHeightMeters,
            graphFingerprintSha256 = graphFingerprint, quality = quality, edges = edges.ToArray()
        };
        input.Validate();
        var relativePath = "runtime-shade-input.json";
        var target = Path.Combine(targetRoot, relativePath);
        File.WriteAllText(target, JsonUtility.ToJson(input));
        files.Add(new EnvironmentCostRuntimeCityPackageFile
        {
            kind = "runtime-shade-input", relativePath = relativePath, bytes = new FileInfo(target).Length,
            sha256 = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(target)
        });
    }

    private static float[] ToLocalPoint(GeoReference reference, JArray coordinate)
    {
        if (coordinate == null || coordinate.Count != 2) throw new InvalidOperationException("Baseline coordinate is invalid.");
        var longitude = (double?)coordinate[0] ?? throw new InvalidOperationException("Baseline longitude is invalid.");
        var latitude = (double?)coordinate[1] ?? throw new InvalidOperationException("Baseline latitude is invalid.");
        var projected = reference.Project(new GeoCoordinate(latitude, longitude, 0.0));
        return new[] { (float)projected.X, (float)projected.Z };
    }

    private static void ValidateBaselineForRuntimeInput(JObject baseline, AnalysisRunConfig analysis)
    {
        if (!string.Equals((string)baseline["schemaVersion"], "environment-cost-analysis-0.2", StringComparison.Ordinal) ||
            !string.Equals((string)baseline["status"], "completed", StringComparison.Ordinal) ||
            !string.Equals((string)baseline["areaId"], analysis.areaId, StringComparison.Ordinal) ||
            (int?)baseline["coordinateZoneId"] != analysis.coordinateZoneId || !Near((double?)baseline["radiusMeters"], analysis.radiusMeters))
            throw new InvalidOperationException("Baseline environment cost does not match the runtime analysis configuration.");
        var center = baseline["center"] as JArray;
        var settings = baseline["settings"] as JObject;
        if (center == null || center.Count != 2 || settings == null || !Near((double?)center[0], analysis.CenterLongitude) ||
            !Near((double?)center[1], analysis.CenterLatitude) || !string.Equals((string)settings["date"], analysis.date, StringComparison.Ordinal) ||
            !string.Equals((string)settings["timezone"], analysis.timezone, StringComparison.Ordinal) ||
            !Near((double?)settings["sampleSpacingMeters"], analysis.sampleSpacingMeters) ||
            !Near((double?)settings["pedestrianHeightMeters"], analysis.pedestrianHeightMeters) ||
            !Near((double?)settings["walkingSpeedMetersPerSecond"], analysis.walkingSpeedMetersPerSecond))
            throw new InvalidOperationException("Baseline environment cost metadata or settings do not match the runtime analysis configuration.");
    }

    private static bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) <= 0.000001;

    private static void CopyToPackage(string source, string targetRoot, string relativePath, string kind,
        List<EnvironmentCostRuntimeCityPackageFile> files)
    {
        if (!EnvironmentCostRuntimeCityPackageManifest.IsSafeRelativePath(relativePath))
            throw new InvalidOperationException($"Unsafe package path: {relativePath}");
        if (!File.Exists(source)) throw new FileNotFoundException("Package source file was not found.", source);
        var target = Path.Combine(targetRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Package target directory is missing."));
        File.Copy(source, target, true);
        files.Add(new EnvironmentCostRuntimeCityPackageFile
        {
            kind = kind,
            relativePath = relativePath.Replace('\\', '/'),
            bytes = new FileInfo(target).Length,
            sha256 = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(target)
        });
    }

    private static EnvironmentCostRuntimeCityPackageSource Source(string kind, string originalPath, string resolvedPath) => new EnvironmentCostRuntimeCityPackageSource
    {
        kind = kind,
        originalPath = originalPath.Replace('\\', '/'),
        sha256 = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(resolvedPath)
    };

    private static string FindCommandLineValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private static EnvironmentCostRuntimeCityPackageSource[] BuildSources(RuntimeCityPackageConfig config, string roadManifestPath, string baselinePath, string sidewalkPath)
    {
        var sources = new List<EnvironmentCostRuntimeCityPackageSource>
        {
            Source("analysis-config", config.analysisConfigPath, config.ResolvePath(config.analysisConfigPath))
        };
        if (roadManifestPath != null) sources.Add(Source("road-network-bundle", config.roadNetworkBundlePath, roadManifestPath));
        if (baselinePath != null) sources.Add(Source("baseline-environment-cost", config.baselineEnvironmentCostPath, baselinePath));
        if (sidewalkPath != null) sources.Add(Source("sidewalk-network-v2", config.sidewalkNetworkPath, sidewalkPath));
        return sources.ToArray();
    }
}

[Serializable]
public sealed class RuntimeCityPackageConfig
{
    public string schemaVersion;
    public string areaId;
    public string displayName;
    public string version;
    public string analysisConfigPath;
    public string roadNetworkBundlePath;
    public string baselineEnvironmentCostPath;
    public string sidewalkNetworkPath;
    public string packageRelativePath;
    public string inspectionSceneAssetPath;
    public string runtimeShadeResultOutputPath;
    public string runtimeShadeCompleteMarkerPath;
    [JsonIgnore] public string repositoryRoot;

    public static RuntimeCityPackageConfig Load(string path)
    {
        var root = FindRepositoryRoot();
        var fullPath = ResolvePath(root, path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Runtime city package config was not found.", fullPath);
        var config = JsonConvert.DeserializeObject<RuntimeCityPackageConfig>(File.ReadAllText(fullPath))
            ?? throw new InvalidOperationException("Runtime city package config could not be parsed.");
        config.repositoryRoot = root;
        config.Validate();
        return config;
    }

    public string ResolvePath(string path) => ResolvePath(repositoryRoot, path);
    public string ResolveOptionalPath(string path) => string.IsNullOrWhiteSpace(path) ? null : ResolvePath(path);
    [JsonIgnore] public bool IsV2SidewalkPackage => string.Equals(schemaVersion, "environment-cost-runtime-city-package-config-0.2", StringComparison.Ordinal);

    private void Validate()
    {
        var legacy = string.Equals(schemaVersion, "environment-cost-runtime-city-package-config-0.1", StringComparison.Ordinal);
        var v2 = string.Equals(schemaVersion, "environment-cost-runtime-city-package-config-0.2", StringComparison.Ordinal);
        if ((!legacy && !v2) ||
            string.IsNullOrWhiteSpace(areaId) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(analysisConfigPath) || !EnvironmentCostRuntimeCityPackageManifest.IsSafeRelativePath(packageRelativePath) ||
            (legacy && (string.IsNullOrWhiteSpace(roadNetworkBundlePath) || string.IsNullOrWhiteSpace(baselineEnvironmentCostPath))) ||
            (v2 && (string.IsNullOrWhiteSpace(sidewalkNetworkPath) || string.IsNullOrWhiteSpace(inspectionSceneAssetPath) ||
                    string.IsNullOrWhiteSpace(runtimeShadeResultOutputPath) || string.IsNullOrWhiteSpace(runtimeShadeCompleteMarkerPath))))
            throw new InvalidOperationException("Runtime city package config is incomplete or invalid.");
    }

    private static string FindRepositoryRoot()
    {
        var current = Directory.GetParent(Application.dataPath)?.Parent;
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root (.git directory) was not found from this Unity project.");
    }

    private static string ResolvePath(string root, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
}
