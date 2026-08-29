using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Builds a standalone city Player only after its StreamingAssets package has passed verification.</summary>
public static class EnvironmentCostRuntimeCityPlayerBuild
{
    /// <summary>Batch entry point: -runtimeCityPackageConfig data/runtime-city-packages/ichigaya-venue.json.</summary>
    public static void Run()
    {
        try
        {
            var value = FindCommandLineValue("-runtimeCityPackageConfig");
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Pass -runtimeCityPackageConfig <path> to Unity.");
            Build(RuntimeCityPackageConfig.Load(value));
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            Debug.LogError("ENVIRONMENT_COST_RUNTIME_CITY_PLAYER_FAILED");
            if (Application.isBatchMode) EditorApplication.Exit(1);
            else throw;
        }
    }

    public static void Build(RuntimeCityPackageConfig config)
    {
        var packageRoot = Path.Combine(Application.streamingAssetsPath, config.packageRelativePath);
        EnvironmentCostRuntimeCityPackageBuilder.Verify(packageRoot);
        var scenePath = $"Assets/Scenes/EnvironmentCostInspection/{config.areaId}.unity";
        if (!File.Exists(scenePath)) throw new FileNotFoundException("Generate the local inspection Scene before building the Runtime player.", scenePath);
        EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        var outputPath = $"Builds/EnvironmentCostRuntime/{config.areaId}/{config.areaId}.exe";
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException($"Runtime city player build failed: {report.summary.result}.");
        Debug.Log($"ENVIRONMENT_COST_RUNTIME_CITY_PLAYER_READY area={config.areaId} path={outputPath} bytes={report.summary.totalSize}");
    }

    private static string FindCommandLineValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }
}
