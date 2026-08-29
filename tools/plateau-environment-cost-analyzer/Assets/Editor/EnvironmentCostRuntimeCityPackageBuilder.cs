using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Creates the versioned StreamingAssets package consumed by the standalone Runtime player.</summary>
public static class EnvironmentCostRuntimeCityPackageBuilder
{
    private const string ConfigSchema = "environment-cost-runtime-city-package-config-0.1";
    private const string PackageSchema = "environment-cost-runtime-city-package-0.1";

    [MenuItem("PLATEAU/Environment Cost/Create Runtime City Package")]
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
        var roadManifestPath = config.ResolvePath(config.roadNetworkBundlePath);
        var baselinePath = config.ResolvePath(config.baselineEnvironmentCostPath);
        if (!File.Exists(roadManifestPath)) throw new FileNotFoundException("Road network bundle manifest was not found.", roadManifestPath);
        if (!File.Exists(baselinePath)) throw new FileNotFoundException("Baseline environment cost was not found.", baselinePath);

        var targetRoot = Path.Combine(Application.streamingAssetsPath, config.packageRelativePath);
        var stagingRoot = targetRoot + ".staging";
        if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var files = new List<EnvironmentCostRuntimeCityPackageFile>();
            CopyToPackage(baselinePath, stagingRoot, "baseline-environment-cost.json", "baseline-environment-cost", files);
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
                inspectionSceneAssetPath = $"Assets/Scenes/EnvironmentCostInspection/{config.areaId}.unity",
                scene = new EnvironmentCostRuntimeCityPackageScene
                {
                    requiredLayers = new[]
                    {
                        new EnvironmentCostRuntimeCityPackageLayer { name = "Building", layer = 8, role = "display-and-raycast-obstacle" },
                        new EnvironmentCostRuntimeCityPackageLayer { name = "Road", layer = 9, role = "display-and-walkable-surface" },
                        new EnvironmentCostRuntimeCityPackageLayer { name = "Terrain", layer = 10, role = "display-and-terrain-surface" }
                    }
                },
                sources = new[]
                {
                    Source("analysis-config", config.analysisConfigPath, config.ResolvePath(config.analysisConfigPath)),
                    Source("road-network-bundle", config.roadNetworkBundlePath, roadManifestPath),
                    Source("baseline-environment-cost", config.baselineEnvironmentCostPath, baselinePath)
                },
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
    public string packageRelativePath;
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

    private void Validate()
    {
        if (!string.Equals(schemaVersion, "environment-cost-runtime-city-package-config-0.1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(areaId) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(analysisConfigPath) || string.IsNullOrWhiteSpace(roadNetworkBundlePath) ||
            string.IsNullOrWhiteSpace(baselineEnvironmentCostPath) || !EnvironmentCostRuntimeCityPackageManifest.IsSafeRelativePath(packageRelativePath))
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
