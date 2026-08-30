using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PLATEAU.Geometries;
using PLATEAU.Native;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Compares baseline and saved Runtime policy routes without an external route server.</summary>
public sealed class EnvironmentCostRuntimeRouteComparisonController : MonoBehaviour
{
    private const int RoadLayer = 9;
    private const int TerrainLayer = 10;
    private enum CaptureTarget { None, Start, End }

    private EnvironmentCostInspectionMetadata metadata;
    private EnvironmentCostRuntimeCityPackageLoader packageLoader;
    private EnvironmentCostRuntimeRouteComparison routeCore;
    private readonly Dictionary<string, EnvironmentCostRuntimePolicyScenario> scenarios = new Dictionary<string, EnvironmentCostRuntimePolicyScenario>(StringComparer.Ordinal);
    private readonly Dictionary<string, EnvironmentCostRuntimeShadeAnalysisResult> results = new Dictionary<string, EnvironmentCostRuntimeShadeAnalysisResult>(StringComparer.Ordinal);
    private EnvironmentCostRuntimeRouteComparisonResult comparison;
    private EnvironmentCostRuntimeRouteCoordinate startCoordinate;
    private EnvironmentCostRuntimeRouteCoordinate endCoordinate;
    private CaptureTarget captureTarget;
    private Transform routeRoot;
    private GameObject startMarker;
    private GameObject endMarker;
    private Camera interactionCamera;
    private string selectedScenarioA;
    private string selectedScenarioB;
    private string selectedTimestamp;
    private string selectedProfile = "shade";
    private string selectedPolicy = "案A";
    private string displayMode = "重ね表示";
    private string status = "都市データパッケージを読み込み中です…";
    private bool isComparing;
    private int comparisonVersion;
    private bool choicesDirty = true;
    private int skippedResultCount;
    private string skippedResultReason;
    private Material routeMaterial;

    public bool IsCapturingRoutePoint => captureTarget != CaptureTarget.None;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToInspectionScene()
    {
        var metadata = FindFirstObjectByType<EnvironmentCostInspectionMetadata>();
        if (metadata != null && metadata.GetComponent<EnvironmentCostRuntimeRouteComparisonController>() == null)
            metadata.gameObject.AddComponent<EnvironmentCostRuntimeRouteComparisonController>();
    }

    private IEnumerator Start()
    {
        metadata = GetComponent<EnvironmentCostInspectionMetadata>();
        packageLoader = GetComponent<EnvironmentCostRuntimeCityPackageLoader>();
        while (packageLoader != null && (packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.NotStarted ||
                                         packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.Loading)) yield return null;
        if (metadata == null || packageLoader == null || packageLoader.State != EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready)
        {
            status = "経路比較には検証済みの都市データパッケージが必要です。";
            yield break;
        }

        status = "道路ネットワークを読み込み中です…";
        Task<EnvironmentCostRuntimeRouteComparison> loadTask;
        try { loadTask = Task.Run(() => EnvironmentCostRuntimeRouteComparison.Load(packageLoader.PackageRootPath)); }
        catch (Exception exception)
        {
            status = $"経路比較を準備できません: {exception.Message}";
            UnityEngine.Debug.LogException(exception);
            yield break;
        }
        while (!loadTask.IsCompleted) yield return null;
        if (loadTask.IsFaulted)
        {
            var exception = loadTask.Exception?.GetBaseException();
            status = $"経路比較を準備できません: {exception.Message}";
            UnityEngine.Debug.LogException(exception);
            yield break;
        }
        if (loadTask.IsCanceled) { status = "道路ネットワークの読み込みを取り消しました。"; yield break; }
        routeCore = loadTask.Result;
        selectedTimestamp = routeCore.AvailableTimestamps.OrderBy(value => value, StringComparer.Ordinal).FirstOrDefault();
        RefreshSavedResults();
        status = results.Count == 0
            ? "比較できる施策解析結果がありません。施策を保存し、全時刻または比較時刻の日陰解析を実行してください。"
            : "案A・案B、比較時刻、起点・終点を指定してください。";
    }

    private void Update()
    {
        if (captureTarget == CaptureTarget.None || EnvironmentCostRuntimeUiInputGate.IsPointerOverUi || !Input.GetMouseButtonDown(0)) return;
        var camera = ResolveCamera();
        if (camera == null) { status = "起終点の指定に使えるカメラがありません。"; return; }
        var mask = (1 << RoadLayer) | (1 << TerrainLayer);
        if (!Physics.Raycast(camera.ScreenPointToRay(Input.mousePosition), out var hit, 5000f, mask, QueryTriggerInteraction.Ignore))
        {
            status = "道路または地表をクリックしてください。";
            return;
        }

        var coordinate = ToGeographic(hit.point);
        var selectedTarget = captureTarget;
        if (selectedTarget == CaptureTarget.Start) startCoordinate = coordinate; else endCoordinate = coordinate;
        ReplaceEndpointMarker(selectedTarget, hit.point);
        var label = captureTarget == CaptureTarget.Start ? "起点" : "終点";
        captureTarget = CaptureTarget.None;
        comparisonVersion++;
        comparison = null;
        ClearRoutes();
        status = $"{label}を設定しました（緯度 {coordinate.latitude:F6}、経度 {coordinate.longitude:F6}）。";
    }

    private Camera ResolveCamera()
    {
        if (interactionCamera != null && interactionCamera.isActiveAndEnabled) return interactionCamera;
        interactionCamera = Camera.main;
        if (interactionCamera == null) interactionCamera = FindFirstObjectByType<Camera>();
        return interactionCamera;
    }

    private EnvironmentCostRuntimeRouteCoordinate ToGeographic(Vector3 position)
    {
        using var reference = CreateLocalReference();
        var coordinate = reference.Unproject(new PlateauVector3d(position.x, position.y, position.z));
        return new EnvironmentCostRuntimeRouteCoordinate { longitude = coordinate.Longitude, latitude = coordinate.Latitude, nodeIndex = -1 };
    }

    private Vector3 ToLocal(EnvironmentCostRuntimeRouteCoordinate coordinate)
    {
        using var reference = CreateLocalReference();
        var point = reference.Project(new GeoCoordinate(coordinate.latitude, coordinate.longitude, 0.0));
        var origin = new Vector3((float)point.X, 500f, (float)point.Z);
        return Physics.Raycast(origin, Vector3.down, out var hit, 1000f, (1 << RoadLayer) | (1 << TerrainLayer), QueryTriggerInteraction.Ignore)
            ? hit.point : new Vector3((float)point.X, 0.5f, (float)point.Z);
    }

    private GeoReference CreateLocalReference()
    {
        using var world = GeoReference.Create(new PlateauVector3d(0, 0, 0), 1f, CoordinateSystem.EUN, metadata.CoordinateZoneId);
        var reference = world.Project(new GeoCoordinate(metadata.Latitude, metadata.Longitude, 0.0));
        return GeoReference.Create(reference, 1f, CoordinateSystem.EUN, metadata.CoordinateZoneId);
    }

    private void RefreshSavedResults()
    {
        scenarios.Clear(); results.Clear();
        skippedResultCount = 0; skippedResultReason = null;
        if (metadata == null) return;
        foreach (var scenarioPath in EnvironmentCostRuntimePolicyScenarioStore.List(metadata.AreaId))
        {
            try
            {
                var scenario = EnvironmentCostRuntimePolicyScenarioStore.Load(scenarioPath);
                var resultPath = Path.Combine(EnvironmentCostRuntimeShadeResultStore.GetDirectory(metadata.AreaId, scenario.id), "latest.json");
                if (!File.Exists(resultPath)) continue;
                var result = EnvironmentCostRuntimeShadeResultStore.LoadForRouteComparison(resultPath);
                ValidateScenarioResult(scenario, result, resultPath);
                scenarios.Add(scenario.id, scenario);
                results.Add(scenario.id, result);
            }
            catch (Exception exception)
            {
                skippedResultCount++;
                skippedResultReason ??= exception.Message;
                UnityEngine.Debug.LogWarning($"ENVIRONMENT_COST_RUNTIME_ROUTE_RESULT_SKIPPED path={scenarioPath} reason={exception.Message}");
            }
        }
        var ids = scenarios.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (string.IsNullOrWhiteSpace(selectedScenarioA) || !scenarios.ContainsKey(selectedScenarioA)) selectedScenarioA = ids.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(selectedScenarioB) && !scenarios.ContainsKey(selectedScenarioB)) selectedScenarioB = null;
        choicesDirty = true;
    }

    private void ValidateScenarioResult(EnvironmentCostRuntimePolicyScenario scenario, EnvironmentCostRuntimeShadeAnalysisResult result, string source)
    {
        if (result == null || result.provenance == null || result.status != "completed") throw new InvalidOperationException("解析結果が未完了です。");
        var manifestPath = Path.Combine(packageLoader.PackageRootPath, "manifest.json");
        var manifestSha = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(manifestPath);
        if (scenario.areaId != metadata.AreaId || scenario.coordinateZoneId != metadata.CoordinateZoneId ||
            scenario.cityPackageVersion != packageLoader.Manifest.version || scenario.cityPackageManifestSha256 != manifestSha)
            throw new InvalidOperationException("シナリオの都市データ版または座標系が一致しません。");
        if (result.areaId != metadata.AreaId || result.provenance.scenarioId != scenario.id ||
            result.provenance.policyFingerprintSha256 != scenario.Fingerprint() ||
            result.provenance.cityPackageVersion != packageLoader.Manifest.version ||
            result.provenance.cityPackageManifestSha256 != manifestSha)
            throw new InvalidOperationException("解析結果の施策または都市データ版が一致しません。");
        if (!string.Equals(result.provenance.resultFingerprintAlgorithm, EnvironmentCostRuntimeShadeResultStore.SemanticFingerprintAlgorithm, StringComparison.Ordinal))
            throw new InvalidOperationException($"解析結果が完全性検証に対応していない旧形式です。fingerprint の有無にかかわらず、対象時刻を再解析してから比較してください: {source}");
        var fingerprint = EnvironmentCostRuntimeShadeResultStore.CalculateSha256(result);
        if (!string.Equals(result.provenance.resultFingerprintSha256, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException($"解析結果fingerprintが一致しません（記録={ShortHash(result.provenance.resultFingerprintSha256)}、再計算={ShortHash(fingerprint)}）: {source}");
    }

    private void RunComparison()
    {
        if (isComparing) return;
        try
        {
            if (routeCore == null) throw new InvalidOperationException("道路ネットワークの読み込みが完了していません。");
            if (startCoordinate == null || endCoordinate == null) throw new InvalidOperationException("起点と終点を地図上で指定してください。");
            if (string.IsNullOrWhiteSpace(selectedScenarioA) || !results.TryGetValue(selectedScenarioA, out var resultA))
                throw new InvalidOperationException("解析済みの案Aを選択してください。");
            if (!string.IsNullOrWhiteSpace(selectedScenarioB) && selectedScenarioA == selectedScenarioB)
                throw new InvalidOperationException("案Aと案Bには異なるシナリオを選択してください。");
            var policies = new List<EnvironmentCostRuntimeShadeAnalysisResult> { resultA };
            if (!string.IsNullOrWhiteSpace(selectedScenarioB)) policies.Add(results[selectedScenarioB]);
            var hour = DateTimeOffset.Parse(selectedTimestamp, CultureInfo.InvariantCulture).Hour;
            foreach (var policy in policies)
                if (policy.provenance.hours == null || !policy.provenance.hours.Contains(hour))
                    throw new InvalidOperationException($"シナリオ「{policy.provenance.scenarioId}」には{hour:00}:00の解析結果がありません。");

            var request = new EnvironmentCostRuntimeRouteComparisonRequest
            {
                areaId = metadata.AreaId, timestamp = selectedTimestamp, start = startCoordinate, end = endCoordinate,
                profiles = new[]
                {
                    new EnvironmentCostRuntimeRouteProfile { id = "shortest", solarAvoidanceFactor = 0.0 },
                    new EnvironmentCostRuntimeRouteProfile { id = "balanced", solarAvoidanceFactor = 0.5 },
                    new EnvironmentCostRuntimeRouteProfile { id = "shade", solarAvoidanceFactor = 2.0 }
                }
            };
            var version = ++comparisonVersion;
            StartCoroutine(RunComparisonAsync(request, policies.ToArray(), version));
        }
        catch (Exception exception)
        {
            status = $"経路比較に失敗しました: {exception.Message}";
            UnityEngine.Debug.LogException(exception);
        }
    }

    private IEnumerator RunComparisonAsync(EnvironmentCostRuntimeRouteComparisonRequest request,
        EnvironmentCostRuntimeShadeAnalysisResult[] policies, int version)
    {
        isComparing = true;
        status = "現状・施策案の経路とKPIを計算中です…";
        var task = Task.Run(() => routeCore.Compare(request, null, policies));
        while (!task.IsCompleted) yield return null;
        isComparing = false;
        if (task.IsFaulted)
        {
            var exception = task.Exception?.GetBaseException();
            status = $"経路比較に失敗しました: {exception?.Message ?? "不明なエラー"}";
            if (exception != null) UnityEngine.Debug.LogException(exception);
            yield break;
        }
        if (task.IsCanceled)
        {
            status = "経路比較を取り消しました。";
            yield break;
        }
        if (version != comparisonVersion)
        {
            status = "比較条件が変更されたため、完了した古い計算結果を破棄しました。比較を再実行してください。";
            yield break;
        }
        comparison = task.Result;
        RenderRoutes();
        status = $"比較が完了しました。現状と{comparison.policies.Count}案を同じ起終点・日時・係数で計算しました。";
    }

    private void RenderRoutes()
    {
        ClearRoutes();
        if (comparison == null) return;
        routeRoot = new GameObject("RuntimeRouteComparison").transform;
        var policyIndex = selectedPolicy == "案B" && comparison.policies.Count > 1 ? 1 : 0;
        if (displayMode != "施策後のみ") CreateRouteLine("現状", RouteFor(comparison.baseline), new Color(0.15f, 0.39f, 0.65f), 1.1f);
        if (displayMode != "現状のみ" && comparison.policies.Count > 0)
            CreateRouteLine(policyIndex == 0 ? "案A" : "案B", RouteFor(comparison.policies[policyIndex]),
                policyIndex == 0 ? new Color(0.0f, 0.58f, 0.55f) : new Color(0.1f, 0.55f, 0.25f), 1.35f);
    }

    private EnvironmentCostRuntimeRoute RouteFor(EnvironmentCostRuntimeRouteScenarioResult scenario)
        => scenario?.routes?.FirstOrDefault(route => route.profile.id == selectedProfile);

    private void CreateRouteLine(string name, EnvironmentCostRuntimeRoute route, Color color, float height)
    {
        if (route == null || route.coordinates == null || route.coordinates.Count == 0) return;
        var item = new GameObject("Route-" + name);
        item.transform.SetParent(routeRoot, false);
        var line = item.AddComponent<LineRenderer>();
        line.useWorldSpace = true; line.widthMultiplier = 1.6f; line.positionCount = route.coordinates.Count;
        line.startColor = color; line.endColor = color; line.numCapVertices = 4; line.numCornerVertices = 2;
        if (routeMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null) routeMaterial = new Material(shader);
        }
        if (routeMaterial != null) line.sharedMaterial = routeMaterial;
        for (var index = 0; index < route.coordinates.Count; index++) line.SetPosition(index, ToLocal(route.coordinates[index]) + Vector3.up * height);
    }

    private void ClearRoutes()
    {
        if (routeRoot != null) Destroy(routeRoot.gameObject);
        routeRoot = null;
    }

    private void ReplaceEndpointMarker(CaptureTarget target, Vector3 position)
    {
        if (target == CaptureTarget.Start)
        {
            DestroyEndpointMarker(ref startMarker);
            startMarker = CreateEndpointMarker("RuntimeRouteStartMarker", position, new Color(0.0f, 0.58f, 0.55f));
        }
        else if (target == CaptureTarget.End)
        {
            DestroyEndpointMarker(ref endMarker);
            endMarker = CreateEndpointMarker("RuntimeRouteEndMarker", position, new Color(0.16f, 0.45f, 0.9f));
        }
    }

    private static GameObject CreateEndpointMarker(string name, Vector3 groundPosition, Color color)
    {
        var marker = new GameObject(name);
        marker.transform.position = groundPosition;
        marker.layer = 2; // Ignore Raycast: markers must not affect subsequent point selection.

        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        var material = shader == null ? null : new Material(shader) { color = color };
        CreateMarkerPrimitive(PrimitiveType.Cylinder, "Post", marker.transform, new Vector3(1.2f, 4.0f, 1.2f), new Vector3(0f, 4.0f, 0f), material);
        CreateMarkerPrimitive(PrimitiveType.Sphere, "Head", marker.transform, new Vector3(2.2f, 2.2f, 2.2f), new Vector3(0f, 8.8f, 0f), material);
        return marker;
    }

    private static void CreateMarkerPrimitive(PrimitiveType primitiveType, string name, Transform parent, Vector3 scale, Vector3 localPosition, Material material)
    {
        var visual = GameObject.CreatePrimitive(primitiveType);
        visual.name = name;
        visual.layer = 2;
        visual.transform.SetParent(parent, false);
        visual.transform.localPosition = localPosition;
        visual.transform.localScale = scale;
        var collider = visual.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
            Destroy(collider);
        }
        var renderer = visual.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }

    private static void DestroyEndpointMarker(ref GameObject marker)
    {
        if (marker == null) return;
        var material = marker.GetComponentInChildren<Renderer>()?.sharedMaterial;
        Destroy(marker);
        if (material != null) Destroy(material);
        marker = null;
    }

    private void ClearEndpointMarkers()
    {
        DestroyEndpointMarker(ref startMarker);
        DestroyEndpointMarker(ref endMarker);
    }

    private void CancelRoutePointSelection()
    {
        captureTarget = CaptureTarget.None;
        status = "起点・終点の指定をキャンセルしました。設定済みの地点とマーカーは保持されます。";
    }

    private void SaveEvidence()
    {
        try
        {
            if (comparison == null) throw new InvalidOperationException("先に経路比較を実行してください。");
            var evidence = new EnvironmentCostRuntimeRouteComparisonEvidence
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), comparison = comparison,
                policyScenarios = comparison.policies.Select(policy => scenarios[policy.scenario.id]).ToList()
            };
            evidence.comparisonFingerprintSha256 = evidence.CalculateFingerprint();
            var directory = Path.Combine(Application.persistentDataPath, "EnvironmentCostComparisons", metadata.AreaId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"comparison-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
            var temporary = path + ".partial";
            File.WriteAllText(temporary, EnvironmentCostRuntimePolicyJson.Serialize(evidence, Formatting.Indented), new UTF8Encoding(false));
            File.Move(temporary, path);
            status = $"比較証跡を保存しました: {path}";
        }
        catch (Exception exception) { status = $"比較証跡を保存できません: {exception.Message}"; }
    }

    public void BuildUi(VisualElement root)
    {
        var panel = new ScrollView(); panel.AddToClassList("runtime-panel"); panel.AddToClassList("runtime-scroll"); root.Add(panel);
        var title = new Label("経路・KPI比較"); title.AddToClassList("runtime-panel-title"); panel.Add(title);
        var scenarioA = new DropdownField("案A"); var scenarioB = new DropdownField("案B（任意）");
        var timestamp = new DropdownField("比較時刻"); panel.Add(scenarioA); panel.Add(scenarioB); panel.Add(timestamp);
        var refresh = new Button(() => { RefreshSavedResults(); InvalidateComparison($"解析済みシナリオを{results.Count}件読み直しました。比較を再実行してください。"); SyncChoices(); }) { text = "解析結果を再読込" }; panel.Add(refresh);
        var points = new VisualElement { style = { flexDirection = FlexDirection.Row } }; panel.Add(points);
        points.Add(new Button(() => { captureTarget = CaptureTarget.Start; status = "道路または地表をクリックして起点を指定してください。"; }) { text = "起点を地図で指定" });
        points.Add(new Button(() => { captureTarget = CaptureTarget.End; status = "道路または地表をクリックして終点を指定してください。"; }) { text = "終点を地図で指定" });
        points.Add(new Button(CancelRoutePointSelection) { text = "指定を取消" });
        var profile = new DropdownField("表示する経路", new List<string> { "最短", "バランス", "日陰優先" }, 2); panel.Add(profile);
        var policy = new DropdownField("表示する施策", new List<string> { "案A", "案B" }, 0); panel.Add(policy);
        var mode = new DropdownField("地図表示", new List<string> { "現状のみ", "施策後のみ", "重ね表示" }, 2); panel.Add(mode);
        var run = new Button(RunComparison) { text = "同一条件で経路・KPIを比較" }; panel.Add(run);
        var kpis = new Label(); kpis.AddToClassList("runtime-status"); panel.Add(kpis);
        panel.Add(new Button(SaveEvidence) { text = "比較証跡をエクスポート" });
        var state = new Label(); state.AddToClassList("runtime-status"); panel.Add(state);

        scenarioA.RegisterValueChangedCallback(change => { selectedScenarioA = DisplayToId(change.newValue); InvalidateComparison("案Aを変更しました。比較を再実行してください。"); });
        scenarioB.RegisterValueChangedCallback(change => { selectedScenarioB = DisplayToId(change.newValue); InvalidateComparison("案Bを変更しました。比較を再実行してください。"); });
        timestamp.RegisterValueChangedCallback(change => { selectedTimestamp = change.newValue; InvalidateComparison("比較時刻を変更しました。比較を再実行してください。"); });
        profile.RegisterValueChangedCallback(change => { selectedProfile = change.newValue == "最短" ? "shortest" : change.newValue == "バランス" ? "balanced" : "shade"; RenderRoutes(); });
        policy.RegisterValueChangedCallback(change => { selectedPolicy = change.newValue; RenderRoutes(); });
        mode.RegisterValueChangedCallback(change => { displayMode = change.newValue; RenderRoutes(); });

        void SyncChoices()
        {
            var available = scenarios.Values.OrderBy(item => item.id, StringComparer.Ordinal).Select(DisplayScenario).ToList();
            scenarioA.choices = available.Count > 0 ? available : new List<string> { "（解析結果なし）" };
            scenarioB.choices = new[] { "（なし）" }.Concat(available).ToList();
            scenarioA.SetValueWithoutNotify(selectedScenarioA != null && scenarios.ContainsKey(selectedScenarioA) ? DisplayScenario(scenarios[selectedScenarioA]) : scenarioA.choices[0]);
            scenarioB.SetValueWithoutNotify(selectedScenarioB != null && scenarios.ContainsKey(selectedScenarioB) ? DisplayScenario(scenarios[selectedScenarioB]) : "（なし）");
            var timestamps = routeCore?.AvailableTimestamps?.OrderBy(value => value, StringComparer.Ordinal).ToList() ?? new List<string>();
            timestamp.choices = timestamps.Count > 0 ? timestamps : new List<string> { "（読込中）" };
            timestamp.SetValueWithoutNotify(selectedTimestamp ?? timestamp.choices[0]);
            choicesDirty = false;
        }
        SyncChoices();
        panel.schedule.Execute(() =>
        {
            if (choicesDirty) SyncChoices();
            var policyChoices = string.IsNullOrWhiteSpace(selectedScenarioB) ? new List<string> { "案A" } : new List<string> { "案A", "案B" };
            if (!policy.choices.SequenceEqual(policyChoices))
            {
                policy.choices = policyChoices;
                if (!policyChoices.Contains(selectedPolicy)) { selectedPolicy = "案A"; policy.SetValueWithoutNotify(selectedPolicy); RenderRoutes(); }
            }
            run.SetEnabled(!isComparing && routeCore != null && results.Count > 0);
            kpis.text = BuildKpiText();
            var skipped = skippedResultCount == 0 ? "" : $"\n注意: 条件不一致の解析結果を{skippedResultCount}件除外しました（{skippedResultReason}）。";
            state.text = $"起点: {CoordinateLabel(startCoordinate)}\n終点: {CoordinateLabel(endCoordinate)}\n{status}{skipped}";
        }).Every(250);
    }

    private string BuildKpiText()
    {
        if (comparison == null) return "比較結果はまだありません。";
        var baseline = RouteFor(comparison.baseline);
        var lines = new List<string> { KpiLine("現状", baseline, null) };
        for (var index = 0; index < comparison.policies.Count; index++)
            lines.Add(KpiLine(index == 0 ? "案A" : "案B", RouteFor(comparison.policies[index]), baseline));
        var factor = comparison.conditions.Profiles().FirstOrDefault(item => item.id == selectedProfile)?.solarAvoidanceFactor ?? 0.0;
        var scenarioLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(selectedScenarioA)) scenarioLines.Add(ScenarioConditionLine("案A", selectedScenarioA, 0));
        if (!string.IsNullOrWhiteSpace(selectedScenarioB)) scenarioLines.Add(ScenarioConditionLine("案B", selectedScenarioB, 1));
        return string.Join("\n", lines) + $"\n比較時刻: {comparison.timestamp}\n日射回避係数: {factor:F2}\n都市データ版: {comparison.cityPackageVersion}\nTopology: {ShortHash(comparison.topologyFingerprintSha256)}\n比較fingerprint: {ShortHash(comparison.comparisonFingerprintSha256)}\n" + string.Join("\n", scenarioLines);
    }

    private string ScenarioConditionLine(string label, string scenarioId, int policyIndex)
    {
        if (!scenarios.TryGetValue(scenarioId, out var scenario) || policyIndex >= comparison.policies.Count) return $"{label}: 読み込み不可";
        var facilities = scenario.facilities == null || scenario.facilities.Count == 0
            ? "なし"
            : string.Join(", ", scenario.facilities.Select(item => $"{item.id}({item.type}, x={item.localPosition.x:F1}, z={item.localPosition.z:F1}, h={item.heightMeters:F1}m)"));
        var source = comparison.policies[policyIndex].scenario;
        return $"{label}: scenario ID={scenario.id}\n入力施設 {scenario.facilities?.Count ?? 0}件: {facilities}\n解析生成: {source.generatedAtUtc}\n結果fingerprint: {ShortHash(source.resultFingerprintSha256)} / 施策fingerprint: {ShortHash(source.policyFingerprintSha256)}";
    }

    private static string KpiLine(string label, EnvironmentCostRuntimeRoute route, EnvironmentCostRuntimeRoute baseline)
    {
        if (route == null) return label + ": 結果なし";
        var shade = route.observedShadeRatio < 0 ? "不明" : $"{route.observedShadeRatio * 100:F2}%";
        var delta = baseline == null || route.observedShadeRatio < 0 || baseline.observedShadeRatio < 0 ? "" :
            $"、現状差 {(route.observedShadeRatio - baseline.observedShadeRatio) * 100:+0.000;-0.000;0.000} pt / 歩行 {route.walkingSeconds - baseline.walkingSeconds:+0.0;-0.0;0.0}秒 / 日向 {route.solarExposureSeconds - baseline.solarExposureSeconds:+0.0;-0.0;0.0}秒";
        return $"{label}: {route.distanceMeters:F0}m、歩行 {route.walkingSeconds:F1}秒、日向 {route.solarExposureSeconds:F1}秒、日陰率 {shade}{delta}（{route.coverageStatus}）";
    }

    private static string CoordinateLabel(EnvironmentCostRuntimeRouteCoordinate coordinate)
        => coordinate == null ? "未設定" : $"{coordinate.latitude:F6}, {coordinate.longitude:F6}";
    private static string DisplayScenario(EnvironmentCostRuntimePolicyScenario scenario) => $"{scenario.displayName} [{scenario.id}]";
    private string DisplayToId(string display) => scenarios.Values.FirstOrDefault(item => DisplayScenario(item) == display)?.id;
    private static string ShortHash(string value) => string.IsNullOrWhiteSpace(value) ? "なし" : value.Substring(0, Math.Min(12, value.Length)) + "…";

    private void InvalidateComparison(string message)
    {
        comparisonVersion++;
        comparison = null;
        ClearRoutes();
        status = message;
    }

    private void OnDestroy()
    {
        ClearRoutes();
        ClearEndpointMarkers();
        if (routeMaterial != null) Destroy(routeMaterial);
    }
}

[Serializable]
public sealed class EnvironmentCostRuntimeRouteComparisonEvidence
{
    public string schemaVersion = EnvironmentCostRuntimeRouteComparison.ResultSchema;
    public string generatedAtUtc;
    public EnvironmentCostRuntimeRouteComparisonResult comparison;
    public List<EnvironmentCostRuntimePolicyScenario> policyScenarios;
    public string comparisonFingerprintSha256;

    public string CalculateFingerprint()
    {
        var previous = comparisonFingerprintSha256;
        comparisonFingerprintSha256 = null;
        try
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(EnvironmentCostRuntimePolicyJson.Serialize(this))))
                .Replace("-", string.Empty).ToLowerInvariant();
        }
        finally { comparisonFingerprintSha256 = previous; }
    }
}
