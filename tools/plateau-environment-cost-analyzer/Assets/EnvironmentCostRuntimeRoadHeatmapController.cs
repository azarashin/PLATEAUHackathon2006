using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PLATEAU.Geometries;
using PLATEAU.Native;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Renders a road-by-road Runtime policy comparison that is bound to the completed route/KPI comparison.</summary>
public sealed class EnvironmentCostRuntimeRoadHeatmapController : MonoBehaviour
{
    private const int RoadLayer = 9;
    private const int TerrainLayer = 10;
    private const float SurfaceRayOriginHeight = 500f;
    private const float SurfaceRayDistance = 1000f;
    private const float HeatmapSurfaceOffset = 1.1f;

    private static readonly Dictionary<string, Color> StatusColors = new Dictionary<string, Color>
    {
        { "improved", new Color(0.05f, 0.62f, 0.36f) }, { "degraded", new Color(0.93f, 0.38f, 0.12f) },
        { "unchanged", new Color(0.45f, 0.55f, 0.65f) }, { "partial", new Color(0.95f, 0.70f, 0.10f) },
        { "missing", new Color(0.25f, 0.28f, 0.32f) }
    };

    private static readonly Dictionary<string, string> StatusLabels = new Dictionary<string, string>
    {
        { "improved", "改善" }, { "degraded", "悪化" }, { "unchanged", "変化なし" },
        { "partial", "一部欠測" }, { "missing", "比較不能" }, { "available", "全データあり" }
    };

    private EnvironmentCostInspectionMetadata metadata;
    private EnvironmentCostRuntimeRouteComparisonController routeComparison;
    private EnvironmentCostRuntimeRoadHeatmapComparisonResult heatmap;
    private Transform heatmapRoot;
    private Material heatmapMaterial;
    private string metric = "shadeRatio";
    private string profileId = "shade";
    private double solarAvoidanceFactor = 2.0;
    private int policyIndex;
    private string selectedRoadId;
    private string status = "経路・KPI比較を実行してから道路別比較を実行してください。";
    private bool isRunning;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToInspectionScene()
    {
        var metadata = FindFirstObjectByType<EnvironmentCostInspectionMetadata>();
        if (metadata != null && metadata.GetComponent<EnvironmentCostRuntimeRoadHeatmapController>() == null)
            metadata.gameObject.AddComponent<EnvironmentCostRuntimeRoadHeatmapController>();
    }

    private IEnumerator Start()
    {
        metadata = GetComponent<EnvironmentCostInspectionMetadata>();
        routeComparison = GetComponent<EnvironmentCostRuntimeRouteComparisonController>();
        while (routeComparison == null)
        {
            routeComparison = GetComponent<EnvironmentCostRuntimeRouteComparisonController>();
            yield return null;
        }
    }

    private void RunComparison()
    {
        if (isRunning) return;
        if (!routeComparison.TryGetRoadHeatmapContext(policyIndex, out var core, out var route, out var policy))
        {
            status = "選択した案を含む経路・KPI比較結果がありません。先に同一条件で比較を実行してください。";
            return;
        }
        var request = new EnvironmentCostRuntimeRoadHeatmapComparisonRequest
        {
            areaId = route.areaId, timestamp = route.timestamp, metric = metric,
            profileId = profileId, solarAvoidanceFactor = solarAvoidanceFactor
        };
        StartCoroutine(RunComparisonAsync(core, request, route, policy));
    }

    private IEnumerator RunComparisonAsync(EnvironmentCostRuntimeRouteComparison core,
        EnvironmentCostRuntimeRoadHeatmapComparisonRequest request, EnvironmentCostRuntimeRouteComparisonResult route,
        EnvironmentCostRuntimeShadeAnalysisResult policy)
    {
        isRunning = true;
        status = "道路別の現状・施策差分を集計中です。";
        var task = Task.Run(() => core.CompareRoadHeatmap(request, route, policy));
        while (!task.IsCompleted) yield return null;
        isRunning = false;
        if (task.IsFaulted)
        {
            status = "道路別比較に失敗しました: " + task.Exception?.GetBaseException().Message;
            if (task.Exception != null) UnityEngine.Debug.LogException(task.Exception.GetBaseException());
            yield break;
        }
        heatmap = task.Result;
        selectedRoadId = heatmap.edges.FirstOrDefault()?.id;
        RenderHeatmap();
        status = $"{heatmap.edges.Count}道路辺を比較しました。{MetricLabel()} の改善/悪化を地図で確認できます。";
    }

    private void RenderHeatmap()
    {
        ClearHeatmap();
        if (heatmap == null || metadata == null) return;
        heatmapRoot = new GameObject("RuntimeRoadHeatmapComparison").transform;
        if (heatmapMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null) heatmapMaterial = new Material(shader);
        }
        using (var reference = CreateLocalReference())
        {
            foreach (var group in heatmap.edges.GroupBy(edge => edge.status))
                CreateStatusMesh(group.Key, group, reference);
        }
    }

    private void CreateStatusMesh(string edgeStatus, IEnumerable<EnvironmentCostRuntimeRoadHeatmapEdge> roads, GeoReference reference)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        foreach (var road in roads)
        {
            var geometry = road.coordinates != null && road.coordinates.Count >= 2
                ? road.coordinates
                : new List<EnvironmentCostRuntimeRouteCoordinate> { road.from, road.to };
            for (var index = 1; index < geometry.Count; index++)
                AddRoadSegment(vertices, triangles, ToLocal(reference, geometry[index - 1]) + Vector3.up * HeatmapSurfaceOffset,
                    ToLocal(reference, geometry[index]) + Vector3.up * HeatmapSurfaceOffset);
        }
        if (vertices.Count == 0) return;
        var item = new GameObject("RoadHeatmap-" + edgeStatus);
        item.transform.SetParent(heatmapRoot, false);
        var mesh = new Mesh { name = "RoadHeatmap-" + edgeStatus };
        if (vertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
        var filter = item.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
        var renderer = item.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = heatmapMaterial == null ? null : new Material(heatmapMaterial) { color = StatusColors[edgeStatus] };
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        item.layer = 2; // Ignore Raycast: the comparison overlay must not obstruct editing or endpoint selection.
    }

    private static void AddRoadSegment(List<Vector3> vertices, List<int> triangles, Vector3 from, Vector3 to)
    {
        var direction = to - from;
        if (direction.sqrMagnitude < 0.0001f) return;
        var side = Vector3.Cross(Vector3.up, direction.normalized) * 0.8f;
        var index = vertices.Count;
        vertices.Add(from - side); vertices.Add(from + side); vertices.Add(to + side); vertices.Add(to - side);
        triangles.Add(index); triangles.Add(index + 1); triangles.Add(index + 2);
        triangles.Add(index); triangles.Add(index + 2); triangles.Add(index + 3);
    }

    private GeoReference CreateLocalReference()
    {
        using var world = GeoReference.Create(new PlateauVector3d(0, 0, 0), 1f, CoordinateSystem.EUN, metadata.CoordinateZoneId);
        var origin = world.Project(new GeoCoordinate(metadata.Latitude, metadata.Longitude, 0.0));
        return GeoReference.Create(origin, 1f, CoordinateSystem.EUN, metadata.CoordinateZoneId);
    }

    private Vector3 ToLocal(GeoReference reference, EnvironmentCostRuntimeRouteCoordinate coordinate)
    {
        var point = reference.Project(new GeoCoordinate(coordinate.latitude, coordinate.longitude, 0.0));
        var projected = new Vector3((float)point.X, 0f, (float)point.Z);
        var rayOrigin = projected + Vector3.up * SurfaceRayOriginHeight;
        var surfaceMask = (1 << RoadLayer) | (1 << TerrainLayer);
        return Physics.Raycast(rayOrigin, Vector3.down, out var hit, SurfaceRayDistance, surfaceMask, QueryTriggerInteraction.Ignore)
            ? hit.point
            : projected + Vector3.up * 0.5f;
    }

    private void SelectRoad(string roadId)
    {
        selectedRoadId = roadId;
        if (heatmap == null || !heatmap.edges.Any(edge => edge.id == roadId)) status = "指定した道路IDは現在の比較結果に含まれていません。";
    }

    private void ExportEvidence()
    {
        try
        {
            if (heatmap == null) throw new InvalidOperationException("道路別比較を実行してください。");
            var directory = Path.Combine(Application.persistentDataPath, "EnvironmentCostRoadHeatmaps", metadata.AreaId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"road-heatmap-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
            var partial = path + ".partial";
            File.WriteAllText(partial, EnvironmentCostRuntimePolicyJson.Serialize(heatmap, Formatting.Indented), new UTF8Encoding(false));
            File.Move(partial, path);
            status = "道路別比較証跡を出力しました: " + path;
        }
        catch (Exception exception) { status = "道路別比較証跡を出力できません: " + exception.Message; }
    }

    public void BuildUi(VisualElement root)
    {
        var panel = new ScrollView(); panel.AddToClassList("runtime-panel"); panel.AddToClassList("runtime-scroll"); root.Add(panel);
        var title = new Label("道路別ヒートマップ比較"); title.AddToClassList("runtime-panel-title"); panel.Add(title);
        panel.Add(new Label("経路・KPI比較と同じ都市パッケージ、時刻、施策結果だけを比較対象にします。"));
        var metricField = new DropdownField("指標", new List<string> { "日陰率", "日射曝露時間", "環境コスト" }, 0); panel.Add(metricField);
        var profileField = new DropdownField("環境コストの経路プロファイル", new List<string> { "最短（係数 0）", "バランス（係数 0.5）", "日陰優先（係数 2）" }, 2); panel.Add(profileField);
        var policyField = new DropdownField("比較する施策", new List<string> { "案A", "案B" }, 0); panel.Add(policyField);
        var run = new Button(RunComparison) { text = "道路別比較を実行" }; panel.Add(run);
        AddLegend(panel);
        var roadId = new TextField("道路ID"); panel.Add(roadId);
        panel.Add(new Button(() => SelectRoad(roadId.value)) { text = "道路を選択" });
        var details = new Label(); details.AddToClassList("runtime-status"); panel.Add(details);
        panel.Add(new Button(ExportEvidence) { text = "道路別比較証跡（JSON）を出力" });
        var state = new Label(); state.AddToClassList("runtime-status"); panel.Add(state);

        metricField.RegisterValueChangedCallback(change =>
        {
            metric = change.newValue == "日陰率" ? "shadeRatio" : change.newValue == "日射曝露時間" ? "solarExposureSeconds" : "environmentCostSeconds";
            profileField.SetEnabled(metric == "environmentCostSeconds");
        });
        profileField.RegisterValueChangedCallback(change =>
        {
            if (change.newValue.StartsWith("最短", StringComparison.Ordinal)) { profileId = "shortest"; solarAvoidanceFactor = 0.0; }
            else if (change.newValue.StartsWith("バランス", StringComparison.Ordinal)) { profileId = "balanced"; solarAvoidanceFactor = 0.5; }
            else { profileId = "shade"; solarAvoidanceFactor = 2.0; }
        });
        profileField.SetEnabled(false);
        policyField.RegisterValueChangedCallback(change => { policyIndex = change.newValue == "案B" ? 1 : 0; });
        panel.schedule.Execute(() =>
        {
            run.SetEnabled(!isRunning);
            var policyChoices = routeComparison != null && routeComparison.CompletedPolicyCount > 1
                ? new List<string> { "案A", "案B" }
                : new List<string> { "案A" };
            if (!policyField.choices.SequenceEqual(policyChoices))
            {
                policyField.choices = policyChoices;
                if (policyIndex > 0) { policyIndex = 0; policyField.SetValueWithoutNotify("案A"); }
            }
            details.text = BuildDetailJapanese();
            state.text = status;
        }).Every(250);
    }

    private string BuildDetailJapanese()
    {
        if (heatmap == null) return "道路を選択すると、現状・施策後・差分・品質状態を表示します。";
        var road = heatmap.edges.FirstOrDefault(item => item.id == selectedRoadId);
        if (road == null) return "道路IDを入力して道路を選択してください。";
        var unit = heatmap.metric == "shadeRatio" ? "%" : "秒";
        var before = heatmap.metric == "shadeRatio" ? road.baselineValue * 100.0 : road.baselineValue;
        var after = heatmap.metric == "shadeRatio" ? road.policyValue * 100.0 : road.policyValue;
        var delta = heatmap.metric == "shadeRatio" ? road.delta * 100.0 : road.delta;
        return $"道路: {road.id}\n現状: {FormatValue(before, unit)}（{StatusLabel(road.baselineStatus)}）\n施策後: {FormatValue(after, unit)}（{StatusLabel(road.policyStatus)}）\n差分: {FormatValue(delta, unit, true)}\n比較状態: {StatusLabel(road.status)}\n歩行時間: {road.walkingSeconds:F1} 秒\nsource edge: {string.Join(", ", road.sourceEdgeIds ?? Array.Empty<string>())}";
    }

    private static void AddLegend(VisualElement panel)
    {
        var legend = new VisualElement();
        legend.style.flexDirection = FlexDirection.Row;
        legend.style.flexWrap = Wrap.Wrap;
        legend.style.marginTop = 4;
        legend.style.marginBottom = 4;
        legend.Add(new Label("凡例: "));
        AddLegendItem(legend, "improved", "緑");
        AddLegendItem(legend, "degraded", "オレンジ");
        AddLegendItem(legend, "unchanged", "青灰");
        AddLegendItem(legend, "partial", "黄");
        AddLegendItem(legend, "missing", "暗灰");
        panel.Add(legend);
    }

    private static void AddLegendItem(VisualElement legend, string comparisonStatus, string colorName)
    {
        var item = new Label($"■ {StatusLabel(comparisonStatus)}（{colorName}）  ");
        item.style.color = StatusColors[comparisonStatus];
        legend.Add(item);
    }

    private static string StatusLabel(string comparisonStatus)
        => !string.IsNullOrEmpty(comparisonStatus) && StatusLabels.TryGetValue(comparisonStatus, out var label)
            ? label
            : "不明";

    private string BuildDetail()
    {
        if (heatmap == null) return "道路を選択すると、現状・施策後・差分・品質状態を表示します。";
        var road = heatmap.edges.FirstOrDefault(item => item.id == selectedRoadId);
        if (road == null) return "道路IDを入力して選択してください。";
        var unit = heatmap.metric == "shadeRatio" ? "%" : "秒";
        var before = heatmap.metric == "shadeRatio" ? road.baselineValue * 100.0 : road.baselineValue;
        var after = heatmap.metric == "shadeRatio" ? road.policyValue * 100.0 : road.policyValue;
        var delta = heatmap.metric == "shadeRatio" ? road.delta * 100.0 : road.delta;
        return $"道路: {road.id}\n現状: {FormatValue(before, unit)} ({road.baselineStatus})\n施策後: {FormatValue(after, unit)} ({road.policyStatus})\n差分: {FormatValue(delta, unit, true)}\n状態: {road.status}\n徒歩時間: {road.walkingSeconds:F1} 秒\nsource edge: {string.Join(", ", road.sourceEdgeIds ?? Array.Empty<string>())}";
    }

    private static string FormatValue(double value, string unit, bool signed = false)
        => value < 0 ? "未計測" : (signed ? value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) : value.ToString("0.00", CultureInfo.InvariantCulture)) + unit;
    private string MetricLabel() => metric == "shadeRatio" ? "日陰率" : metric == "solarExposureSeconds" ? "日射曝露時間" : $"環境コスト（{profileId}, 係数 {solarAvoidanceFactor:0.##}）";

    private void ClearHeatmap()
    {
        if (heatmapRoot == null) return;
        foreach (var filter in heatmapRoot.GetComponentsInChildren<MeshFilter>()) if (filter.sharedMesh != null) Destroy(filter.sharedMesh);
        foreach (var renderer in heatmapRoot.GetComponentsInChildren<MeshRenderer>()) if (renderer.sharedMaterial != null) Destroy(renderer.sharedMaterial);
        Destroy(heatmapRoot.gameObject); heatmapRoot = null;
    }

    private void OnDestroy()
    {
        ClearHeatmap();
        if (heatmapMaterial != null) Destroy(heatmapMaterial);
    }
}
