using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;

/// <summary>Builds the locally generated inspection Scene as a Windows player for #4 verification.</summary>
public static class EnvironmentCostInspectionBuild
{
    private const string ScenePath = "Assets/Scenes/EnvironmentCostInspection.unity";
    private const string OutputPath = "Builds/EnvironmentCostInspection/EnvironmentCostInspection.exe";

    [MenuItem("PLATEAU/Environment Cost/Build Inspection Player (Windows)")]
    public static void BuildWindowsPlayer()
    {
        if (!File.Exists(Path.Combine(Directory.GetParent(UnityEngine.Application.dataPath)?.FullName ?? string.Empty,
                "Assets", "Scenes", "EnvironmentCostInspection.unity")))
        {
            throw new FileNotFoundException("Create the inspection Scene before building a player.", ScenePath);
        }

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = OutputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException($"Inspection player build failed: {report.summary.result}.");
        UnityEngine.Debug.Log($"ENVIRONMENT_COST_INSPECTION_PLAYER_READY path={OutputPath} bytes={report.summary.totalSize}");
    }
}
