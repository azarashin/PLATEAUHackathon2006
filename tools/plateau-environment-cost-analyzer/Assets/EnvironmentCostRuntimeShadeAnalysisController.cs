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
    [SerializeField, TextArea] private string statusMessage = "都市データパッケージの確認を待機しています。";

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
            statusMessage = "日陰解析には都市データパッケージと検証シーン情報が必要です。";
            yield break;
        }
        statusMessage = packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready
            ? "日陰解析の準備ができました。時刻を選択して実行してください。"
            : $"日陰解析を利用できません: {packageLoader.StatusMessage}";
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
            statusMessage = $"日陰解析が完了しました: {selectedHour:00}:00、{LatestResult.edges.Count:N0}辺、{stopwatch.Elapsed.TotalSeconds:F1}秒。";
            UnityEngine.Debug.Log($"ENVIRONMENT_COST_RUNTIME_SHADE_ANALYSIS_READY area={metadata.AreaId} hour={selectedHour} edges={LatestResult.edges.Count} seconds={stopwatch.Elapsed.TotalSeconds:F3}");
        }
        catch (Exception exception)
        {
            statusMessage = $"日陰解析に失敗しました: {exception.Message}";
            UnityEngine.Debug.LogException(exception);
            UnityEngine.Debug.LogError("ENVIRONMENT_COST_RUNTIME_SHADE_ANALYSIS_FAILED");
        }
    }

    /// <summary>Called by the Runtime policy editor when Building-layer policy geometry changes.</summary>
    public void InvalidateForPolicyChange(string scenarioId)
    {
        if (LatestResult == null) return;
        LatestResult = null;
        statusMessage = $"施策シナリオ「{scenarioId}」を変更しました。結果を更新するには日陰解析を再実行してください。";
        UnityEngine.Debug.Log($"ENVIRONMENT_COST_RUNTIME_SHADE_ANALYSIS_INVALIDATED scenario={scenarioId}");
    }

    private void OnGUI()
    {
        if (!Application.isPlaying) return;
        GUILayout.BeginArea(new Rect(16f, 198f, 430f, 116f), GUI.skin.box);
        GUILayout.Label("日陰解析");
        selectedHour = Mathf.RoundToInt(GUILayout.HorizontalSlider(selectedHour, 0f, 23f));
        GUILayout.Label($"解析時刻: {selectedHour:00}:00");
        var originalEnabled = GUI.enabled;
        GUI.enabled = originalEnabled && packageLoader != null && packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready;
        if (GUILayout.Button("選択時刻の全道路解析を実行")) RunSelectedHour();
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
