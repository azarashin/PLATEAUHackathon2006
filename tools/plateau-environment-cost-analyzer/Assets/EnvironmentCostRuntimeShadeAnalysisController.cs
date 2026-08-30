using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Runs full or conservative policy-impact recalculation without blocking the Player frame loop.</summary>
public sealed class EnvironmentCostRuntimeShadeAnalysisController : MonoBehaviour
{
    private const double FrameBudgetMilliseconds = 12.0;
    private const double ProgressRefreshSeconds = 0.2;
    [SerializeField] private EnvironmentCostRuntimeCityPackageLoader packageLoader;
    [SerializeField] private EnvironmentCostInspectionMetadata metadata;
    [SerializeField] private int selectedHour = 12;
    [SerializeField, TextArea] private string statusMessage = "都市データパッケージの確認を待機しています。";

    private readonly List<EnvironmentCostRuntimePolicyFacility> changedFacilities = new List<EnvironmentCostRuntimePolicyFacility>();
    private readonly List<Camera> suspendedSceneCameras = new List<Camera>();
    private Coroutine activeRun;
    private bool cancellationRequested;
    private bool requiresFullRecalculation;
    private bool isLatestResultCurrent;
    private int runVersion;
    private int completedEdges;
    private int totalEdges;
    private int recalculatedEdges;
    private string activeScope;
    private double lastElapsedSeconds = -1.0;
    private string lastCompletedScope;

    public EnvironmentCostRuntimeShadeAnalysisResult LatestResult { get; private set; }
    public bool IsRunning => activeRun != null;
    public bool IsLatestResultCurrent => isLatestResultCurrent;

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
        RunHours(new[] { selectedHour });
    }

    public void RunAllHours()
    {
        RunHours(Enumerable.Range(0, 24).ToArray());
    }

    private void RunHours(int[] hours)
    {
        if (IsRunning || packageLoader == null || packageLoader.State != EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready || metadata == null) return;
        SuspendSceneRendering();
        activeRun = StartCoroutine(RunHoursAsync(++runVersion, hours));
    }

    public void CancelCurrentRun()
    {
        if (!IsRunning) return;
        cancellationRequested = true;
        statusMessage = "取消を要求しました。現在の道路辺の処理後に停止します。";
    }

    /// <summary>Called by the policy editor. Previous/current geometry are both retained so removals remain safe.</summary>
    public void InvalidateForPolicyChange(string scenarioId, EnvironmentCostRuntimePolicyFacility previous = null,
        EnvironmentCostRuntimePolicyFacility current = null, bool forceFullRecalculation = false)
    {
        if (previous != null) changedFacilities.Add(CloneFacility(previous));
        if (current != null) changedFacilities.Add(CloneFacility(current));
        requiresFullRecalculation |= forceFullRecalculation || (previous == null && current == null);
        isLatestResultCurrent = false;
        runVersion++;
        if (IsRunning) cancellationRequested = true;
        statusMessage = $"施策シナリオ「{scenarioId}」を変更しました。日陰解析を再実行してください。";
        UnityEngine.Debug.Log($"ENVIRONMENT_COST_RUNTIME_SHADE_ANALYSIS_INVALIDATED scenario={scenarioId} full={requiresFullRecalculation} changedFacilities={changedFacilities.Count}");
    }

    private IEnumerator RunHoursAsync(int version, int[] hours)
    {
        cancellationRequested = false;
        try
        {
            var inputPath = Path.Combine(packageLoader.PackageRootPath ?? throw new InvalidOperationException("検証済みの都市データパッケージのルートが利用できません。"),
                "runtime-shade-input.json");
            var input = JsonUtility.FromJson<EnvironmentCostRuntimeShadeAnalysisInput>(File.ReadAllText(inputPath));
            input.Validate();
            var request = new EnvironmentCostRuntimeShadeAnalysisRequest
            {
                analysisDate = DateTime.ParseExact(metadata.AnalysisDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), hours = hours
            };
            request.Validate(input);

            var previousById = LatestResult?.edges?.ToDictionary(edge => edge.id, StringComparer.Ordinal);
            var canReusePrevious = !requiresFullRecalculation && LatestResult != null && !isLatestResultCurrent &&
                LatestResult.status == "completed" && LatestResult.provenance != null && LatestResult.provenance.hours != null &&
                LatestResult.provenance.hours.SequenceEqual(hours) && previousById != null && previousById.Count == input.edges.Length;
            var affectedIds = canReusePrevious
                ? EnvironmentCostRuntimePolicyImpact.FindAffectedEdgeIds(input, request, changedFacilities)
                : new HashSet<string>(input.edges.Select(edge => edge.id), StringComparer.Ordinal);
            activeScope = canReusePrevious ? "局所再計算" : "全範囲再計算";
            totalEdges = input.edges.Length;
            recalculatedEdges = affectedIds.Count;
            completedEdges = 0;
            var stopwatch = Stopwatch.StartNew();
            var frameStopwatch = Stopwatch.StartNew();
            var lastProgressRefreshSeconds = 0.0;
            var edgesInCurrentFrame = 0;
            var result = EnvironmentCostRuntimeShadeAnalyzer.CreateResult(input, request);
            ApplyProvenance(result, activeScope, totalEdges, recalculatedEdges);

            foreach (var edge in input.edges)
            {
                if (cancellationRequested || version != runVersion)
                {
                    statusMessage = "日陰解析を取り消しました。途中の結果は保存しません。";
                    yield break;
                }
                result.edges.Add(affectedIds.Contains(edge.id) || !canReusePrevious
                    ? EnvironmentCostRuntimeShadeAnalyzer.AnalyzeEdge(input, edge, request)
                    : previousById[edge.id]);
                completedEdges++;
                edgesInCurrentFrame++;
                if (stopwatch.Elapsed.TotalSeconds - lastProgressRefreshSeconds >= ProgressRefreshSeconds)
                {
                    statusMessage = $"{activeScope}を実行中: {completedEdges:N0}/{totalEdges:N0} 辺（再計算 {recalculatedEdges:N0} 辺、直近フレーム {edgesInCurrentFrame:N0} 辺）";
                    lastProgressRefreshSeconds = stopwatch.Elapsed.TotalSeconds;
                }
                if (frameStopwatch.Elapsed.TotalMilliseconds < FrameBudgetMilliseconds) continue;
                yield return null;
                frameStopwatch.Restart();
                edgesInCurrentFrame = 0;
            }

            stopwatch.Stop();
            if (cancellationRequested || version != runVersion)
            {
                statusMessage = "日陰解析を取り消しました。途中の結果は保存しません。";
                yield break;
            }
            result.provenance.resultFingerprintSha256 = EnvironmentCostRuntimeShadeResultStore.CalculateSha256(result);
            var policy = GetComponent<EnvironmentCostRuntimePolicyScenarioController>()?.Scenario;
            var scenarioId = policy?.id ?? "baseline";
            var savedPath = EnvironmentCostRuntimeShadeResultStore.Save(result, scenarioId);
            LatestResult = result;
            isLatestResultCurrent = true;
            changedFacilities.Clear();
            requiresFullRecalculation = false;
            lastElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            lastCompletedScope = activeScope;
            statusMessage = $"日陰解析が完了しました: {activeScope}、{recalculatedEdges:N0}/{totalEdges:N0} 辺、{stopwatch.Elapsed.TotalSeconds:F1}秒。証跡: {savedPath}";
            UnityEngine.Debug.Log($"ENVIRONMENT_COST_RUNTIME_SHADE_ANALYSIS_READY area={metadata.AreaId} hour={selectedHour} scope={activeScope} edges={totalEdges} recalculated={recalculatedEdges} seconds={stopwatch.Elapsed.TotalSeconds:F3} fingerprint={result.provenance.resultFingerprintSha256}");
        }
        finally
        {
            ResumeSceneRendering();
            activeRun = null;
        }
    }

    private void SuspendSceneRendering()
    {
        suspendedSceneCameras.Clear();
        foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            if (!camera.enabled || camera.GetComponent<EnvironmentCostInspectionFlyCamera>() == null) continue;
            camera.enabled = false;
            suspendedSceneCameras.Add(camera);
        }
    }

    private void ResumeSceneRendering()
    {
        foreach (var camera in suspendedSceneCameras)
            if (camera != null) camera.enabled = true;
        suspendedSceneCameras.Clear();
    }

    private void ApplyProvenance(EnvironmentCostRuntimeShadeAnalysisResult result, string scope, int edgeCount, int recalculatedCount)
    {
        var policy = GetComponent<EnvironmentCostRuntimePolicyScenarioController>()?.Scenario;
        var manifestPath = Path.Combine(packageLoader.PackageRootPath, "manifest.json");
        result.provenance.scenarioId = policy?.id ?? "baseline";
        result.provenance.policyFingerprintSha256 = policy?.Fingerprint();
        result.provenance.cityPackageVersion = packageLoader.Manifest?.version;
        result.provenance.cityPackageManifestSha256 = File.Exists(manifestPath)
            ? EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(manifestPath) : null;
        result.provenance.recalculationScope = scope;
        result.provenance.totalEdgeCount = edgeCount;
        result.provenance.recalculatedEdgeCount = recalculatedCount;
    }

    private static EnvironmentCostRuntimePolicyFacility CloneFacility(EnvironmentCostRuntimePolicyFacility source)
        => source == null ? null : EnvironmentCostRuntimePolicyJson.Deserialize<EnvironmentCostRuntimePolicyFacility>(EnvironmentCostRuntimePolicyJson.Serialize(source));

    public void BuildUi(VisualElement root)
    {
        var panel = new VisualElement(); panel.AddToClassList("runtime-panel"); root.Add(panel);
        var title = new Label("日陰解析"); title.AddToClassList("runtime-panel-title"); panel.Add(title);
        var hour = new SliderInt("解析時刻", 0, 23) { value = selectedHour }; panel.Add(hour);
        hour.RegisterValueChangedCallback(change => selectedHour = change.newValue);
        var commands = new VisualElement { style = { flexDirection = FlexDirection.Row } }; panel.Add(commands);
        var selectedButton = new Button(RunSelectedHour) { text = "選択時刻を解析" }; commands.Add(selectedButton);
        var allButton = new Button(RunAllHours) { text = "全時刻を解析" }; commands.Add(allButton);
        var cancel = new Button(CancelCurrentRun) { text = "解析を取り消す" }; panel.Add(cancel);
        var status = new Label(); status.AddToClassList("runtime-status"); panel.Add(status);
        panel.schedule.Execute(() =>
        {
            var ready = !IsRunning && packageLoader != null && packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready;
            selectedButton.SetEnabled(ready); allButton.SetEnabled(ready); cancel.style.display = IsRunning ? DisplayStyle.Flex : DisplayStyle.None;
            status.text = $"解析時刻: {selectedHour:00}:00" + (IsRunning ? "\n解析中は3D表示とカメラ操作を一時停止しています。" : "") +
                (lastElapsedSeconds >= 0.0 ? $"\n前回の解析時間: {lastElapsedSeconds:F1}秒（{lastCompletedScope}）" : "") + $"\n{statusMessage}";
        }).Every(100);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToLegacyInspectionScene()
    {
        var sceneMetadata = FindFirstObjectByType<EnvironmentCostInspectionMetadata>();
        if (sceneMetadata != null && sceneMetadata.GetComponent<EnvironmentCostRuntimeShadeAnalysisController>() == null)
            sceneMetadata.gameObject.AddComponent<EnvironmentCostRuntimeShadeAnalysisController>();
    }
}
