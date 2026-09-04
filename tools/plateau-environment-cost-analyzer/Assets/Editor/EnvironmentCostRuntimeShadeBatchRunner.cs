using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Reproducibly creates a v2 Runtime package and calculates all local hours against its inspection scene.</summary>
public static class EnvironmentCostRuntimeShadeBatchRunner
{
    private const string CompleteSchema = "environment-cost-runtime-shade-batch-complete-0.1";

    /// <summary>Batch entry point: -runtimeCityPackageConfig data/runtime-city-packages/&lt;area&gt;-sidewalk-v2.json.</summary>
    public static void Run()
    {
        try
        {
            var argument = FindCommandLineValue("-runtimeCityPackageConfig");
            if (string.IsNullOrWhiteSpace(argument)) throw new ArgumentException("Pass -runtimeCityPackageConfig <path> to Unity.");
            Run(RuntimeCityPackageConfig.Load(argument));
            Debug.Log("ENVIRONMENT_COST_RUNTIME_SHADE_BATCH_COMPLETE");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            Debug.LogError("ENVIRONMENT_COST_RUNTIME_SHADE_BATCH_FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(1);
            else throw;
        }
    }

    public static void Run(RuntimeCityPackageConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (!config.IsV2SidewalkPackage) throw new InvalidOperationException("Runtime shade batch requires a v0.2 sidewalk package config.");

        // Normal Editor mode restores the last open inspection Scene before invoking
        // -executeMethod.  Opening another city directly can temporarily retain both
        // large CityGML scenes and exhaust native memory, so release the restored scene
        // through a lightweight empty scene first.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorUtility.UnloadUnusedAssetsImmediate();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Debug.Log($"ENVIRONMENT_COST_RUNTIME_SHADE_BATCH_CLEAN_SCENE area={config.areaId}");

        EnvironmentCostRuntimeCityPackageBuilder.Create(config);
        var packageRoot = Path.Combine(Application.streamingAssetsPath, config.packageRelativePath);
        EnvironmentCostRuntimeCityPackageBuilder.Verify(packageRoot);
        EditorSceneManager.OpenScene(config.inspectionSceneAssetPath, OpenSceneMode.Single);
        Physics.SyncTransforms();

        var inputPath = Path.Combine(packageRoot, "runtime-shade-input.json");
        var input = EnvironmentCostRuntimePolicyJson.Deserialize<EnvironmentCostRuntimeShadeAnalysisInput>(File.ReadAllText(inputPath));
        input.Validate();
        if (!string.Equals(input.areaId, config.areaId, StringComparison.Ordinal) || input.quality == null ||
            !string.Equals(input.quality.status, "accepted", StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime shade input does not match the accepted v2 package.");

        var request = new EnvironmentCostRuntimeShadeAnalysisRequest
        {
            analysisDate = DateTime.ParseExact(input.analysisDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            hours = Enumerable.Range(0, 24).ToArray()
        };
        var progressInterval = Math.Max(1, input.edges.Length / 100);
        var result = EnvironmentCostRuntimeShadeAnalyzer.Analyze(input, request, onEdgeCompleted: (completed, total) =>
        {
            if (completed == total || completed % progressInterval == 0)
                Debug.Log($"ENVIRONMENT_COST_RUNTIME_SHADE_BATCH_PROGRESS area={input.areaId} edges={completed}/{total}");
        });
        result.provenance.scenarioId = "baseline";
        result.provenance.recalculationScope = "batch-full-24-hours";
        result.provenance.totalEdgeCount = input.edges.Length;
        result.provenance.recalculatedEdgeCount = input.edges.Length;
        result.provenance.cityPackageVersion = JsonUtility.FromJson<EnvironmentCostRuntimeCityPackageManifest>(File.ReadAllText(Path.Combine(packageRoot, "manifest.json"))).version;
        result.provenance.cityPackageManifestSha256 = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(Path.Combine(packageRoot, "manifest.json"));
        // Full-city, all-hour evidence can exceed the Runtime comparison UI's
        // 256 MB loading limit.  The batch contract is an export pipeline, not a
        // UI comparison save, so fingerprint and validate it in memory instead.
        result.provenance.resultFingerprintAlgorithm = EnvironmentCostRuntimeShadeResultStore.SemanticFingerprintAlgorithm;
        result.provenance.resultFingerprintSha256 = EnvironmentCostRuntimeShadeResultStore.CalculateSha256(result);
        ValidateBatchResult(input, result);

        var outputPath = config.ResolvePath(config.runtimeShadeResultOutputPath);
        AtomicWrite(outputPath, EnvironmentCostRuntimePolicyJson.Serialize(result, Formatting.Indented));
        var output = EnvironmentCostRuntimePolicyJson.Deserialize<EnvironmentCostRuntimeShadeAnalysisResult>(File.ReadAllText(outputPath));
        ValidateBatchResult(input, output);
        var marker = new RuntimeShadeBatchCompleteMarker
        {
            schemaVersion = CompleteSchema,
            areaId = input.areaId,
            runtimeShadeInputSha256 = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(inputPath),
            resultSha256 = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(outputPath),
            resultFingerprintSha256 = output.provenance.resultFingerprintSha256,
            graphFingerprintSha256 = input.graphFingerprintSha256,
            generatedAtUtc = DateTime.UtcNow.ToString("O")
        };
        AtomicWrite(config.ResolvePath(config.runtimeShadeCompleteMarkerPath), JsonConvert.SerializeObject(marker, Formatting.Indented));
        Debug.Log($"ENVIRONMENT_COST_RUNTIME_SHADE_BATCH_READY area={input.areaId} edges={input.edges.Length} output={outputPath} fingerprint={marker.resultFingerprintSha256}");
    }

    internal static void ValidateBatchResult(EnvironmentCostRuntimeShadeAnalysisInput input, EnvironmentCostRuntimeShadeAnalysisResult result)
    {
        if (input == null || result == null || result.provenance == null || result.edges == null ||
            result.status != "completed" || result.edges.Count != input.edges.Length ||
            !string.Equals(result.areaId, input.areaId, StringComparison.Ordinal) ||
            !string.Equals(result.provenance.graphFingerprintSha256, input.graphFingerprintSha256, StringComparison.Ordinal) ||
            result.provenance.networkQuality == null || result.provenance.networkQuality.status != "accepted" ||
            !string.Equals(result.provenance.networkQuality.qualityContractVersion, input.quality.qualityContractVersion, StringComparison.Ordinal) ||
            !string.Equals(result.provenance.networkQuality.sourceSchemaVersion, input.quality.sourceSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(result.provenance.resultFingerprintAlgorithm, EnvironmentCostRuntimeShadeResultStore.SemanticFingerprintAlgorithm, StringComparison.Ordinal) ||
            !string.Equals(result.provenance.resultFingerprintSha256, EnvironmentCostRuntimeShadeResultStore.CalculateSha256(result), StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime shade batch result does not match its v2 input or fingerprint.");
        var expected = input.edges.Select(edge => edge.id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var actual = result.edges.Select(edge => edge?.id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal) || result.edges.Any(edge => edge == null || edge.hourly == null || edge.hourly.Length != 24 ||
                edge.hourly.Select(hour => hour.hour).OrderBy(hour => hour).Where((hour, index) => hour != index).Any()))
            throw new InvalidOperationException("Runtime shade batch result does not cover every v2 physical edge and hour exactly once.");
    }

    private static void AtomicWrite(string target, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Output directory is missing."));
        var temporary = target + ".partial";
        File.WriteAllText(temporary, contents);
        if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
    }

    private static string FindCommandLineValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    [Serializable]
    private sealed class RuntimeShadeBatchCompleteMarker
    {
        public string schemaVersion;
        public string areaId;
        public string runtimeShadeInputSha256;
        public string resultSha256;
        public string resultFingerprintSha256;
        public string graphFingerprintSha256;
        public string generatedAtUtc;
    }
}
