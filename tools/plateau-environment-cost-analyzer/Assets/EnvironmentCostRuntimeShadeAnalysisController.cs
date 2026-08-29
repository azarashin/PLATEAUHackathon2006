using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>Player-facing entry point for the Runtime shade core. Persistence and incremental execution are #63 concerns.</summary>
public sealed class EnvironmentCostRuntimeShadeAnalysisController : MonoBehaviour
{
    [SerializeField] private EnvironmentCostRuntimeCityPackageLoader packageLoader;
    [SerializeField] private EnvironmentCostInspectionMetadata metadata;
    [SerializeField] private int selectedHour = 12;
    [SerializeField, TextArea] private string statusMessage = "Runtime shade analysis is waiting for the city package.";

    public EnvironmentCostRuntimeShadeAnalysisResult LatestResult { get; private set; }

    private void Start() => StartCoroutine(LoadInputWhenPackageReady());

    private IEnumerator LoadInputWhenPackageReady()
    {
        packageLoader ??= GetComponent<EnvironmentCostRuntimeCityPackageLoader>();
        metadata ??= GetComponent<EnvironmentCostInspectionMetadata>();
        while (packageLoader != null && (packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.NotStarted ||
                                         packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.Loading)) yield return null;
        if (packageLoader == null || metadata == null)
        {
            statusMessage = "Runtime shade analysis requires city package and inspection metadata components.";
            yield break;
        }
        statusMessage = packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready
            ? "Runtime shade analysis is ready. Select an hour and run it."
            : $"Runtime shade analysis is unavailable: {packageLoader.StatusMessage}";
    }

    public void RunSelectedHour()
    {
        if (packageLoader == null || packageLoader.State != EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready || metadata == null) return;
        try
        {
            var inputPath = Path.Combine(packageLoader.PackageRootPath ?? throw new InvalidOperationException("Verified package root is unavailable."),
                "runtime-shade-input.json");
            var input = JsonUtility.FromJson<EnvironmentCostRuntimeShadeAnalysisInput>(File.ReadAllText(inputPath));
            var date = DateTime.ParseExact(metadata.AnalysisDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var stopwatch = Stopwatch.StartNew();
            LatestResult = EnvironmentCostRuntimeShadeAnalyzer.Analyze(input, new EnvironmentCostRuntimeShadeAnalysisRequest
            {
                analysisDate = date, hours = new[] { selectedHour }
            });
            stopwatch.Stop();
            statusMessage = $"Runtime shade analysis completed: {LatestResult.edges.Count:N0} edges at {selectedHour:00}:00 in {stopwatch.Elapsed.TotalSeconds:F1}s.";
            UnityEngine.Debug.Log($"ENVIRONMENT_COST_RUNTIME_SHADE_ANALYSIS_READY area={metadata.AreaId} hour={selectedHour} edges={LatestResult.edges.Count} seconds={stopwatch.Elapsed.TotalSeconds:F3}");
        }
        catch (Exception exception)
        {
            statusMessage = $"Runtime shade analysis failed: {exception.Message}";
            UnityEngine.Debug.LogException(exception);
            UnityEngine.Debug.LogError("ENVIRONMENT_COST_RUNTIME_SHADE_ANALYSIS_FAILED");
        }
    }

    private void OnGUI()
    {
        if (!Application.isPlaying) return;
        GUILayout.BeginArea(new Rect(16f, 198f, 430f, 116f), GUI.skin.box);
        GUILayout.Label("Runtime Shade Analysis");
        selectedHour = Mathf.RoundToInt(GUILayout.HorizontalSlider(selectedHour, 0f, 23f));
        GUILayout.Label($"Analysis hour: {selectedHour:00}:00");
        var originalEnabled = GUI.enabled;
        GUI.enabled = originalEnabled && packageLoader != null && packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready;
        if (GUILayout.Button("Run full-road analysis for selected hour")) RunSelectedHour();
        GUI.enabled = originalEnabled;
        GUILayout.Label(statusMessage);
        GUILayout.EndArea();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToLegacyInspectionScene()
    {
        var sceneMetadata = FindFirstObjectByType<EnvironmentCostInspectionMetadata>();
        if (sceneMetadata != null && sceneMetadata.GetComponent<EnvironmentCostRuntimeShadeAnalysisController>() == null)
            sceneMetadata.gameObject.AddComponent<EnvironmentCostRuntimeShadeAnalysisController>();
    }
}
