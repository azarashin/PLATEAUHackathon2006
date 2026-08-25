using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;

/// <summary>Builds the locally generated inspection Scene as a Windows player for #4 verification.</summary>
public static class EnvironmentCostInspectionBuild
{
    private const string SceneDirectory = "Assets/Scenes/EnvironmentCostInspection/";

    [MenuItem("PLATEAU/Environment Cost/Build Inspection Player (Windows)")]
    public static void BuildWindowsPlayer()
    {
        var scenePath = EditorSceneManager.GetActiveScene().path;
        if (string.IsNullOrWhiteSpace(scenePath) || !scenePath.StartsWith(SceneDirectory, StringComparison.Ordinal) ||
            !scenePath.EndsWith(".unity", StringComparison.Ordinal) || !File.Exists(scenePath))
        {
            throw new FileNotFoundException("Open a generated city inspection Scene before building a player.", scenePath);
        }

        var areaId = Path.GetFileNameWithoutExtension(scenePath);
        var outputPath = $"Builds/EnvironmentCostInspection/{areaId}/{areaId}.exe";

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException($"Inspection player build failed: {report.summary.result}.");
        UnityEngine.Debug.Log($"ENVIRONMENT_COST_INSPECTION_PLAYER_READY area={areaId} scene={scenePath} path={outputPath} bytes={report.summary.totalSize}");
    }
}
