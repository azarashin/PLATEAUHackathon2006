using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PLATEAU.Geometries;
using PLATEAU.Native;
using UnityEngine;

/// <summary>Player UI and map interaction for tree, shade, and obstacle policy scenarios.</summary>
public sealed class EnvironmentCostRuntimePolicyScenarioController : MonoBehaviour
{
    private const int BuildingLayer = 8;
    private const int RoadLayer = 9;
    private const int TerrainLayer = 10;
    private EnvironmentCostInspectionMetadata metadata;
    private EnvironmentCostRuntimeCityPackageLoader packageLoader;
    private EnvironmentCostRuntimeShadeAnalysisController shadeAnalysis;
    private EnvironmentCostRuntimePolicyScenario scenario;
    private EnvironmentCostRuntimePolicyFacility selected;
    private Transform scenarioRoot;
    private bool placeMode;
    private bool dragging;
    private bool dirty;
    private EnvironmentCostRuntimePolicyFacility lastValidSelected;
    private Camera interactionCamera;
    private string status = "施策シナリオエディターを読み込み中です…";
    private string selectedType = "tree";
    private string scenarioIdInput = "runtime-scenario";
    private string displayNameInput = "新しいシナリオ";
    private string authorInput = "";
    private string memoInput = "";
    private Vector2 scroll;

    public EnvironmentCostRuntimePolicyScenario Scenario => scenario;
    public bool IsDirty => dirty;

    private void Start() => StartCoroutine(InitializeWhenPackageReady());

    // Local inspection Scenes generated before #62 remain useful Player inputs.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToLegacyInspectionScene()
    {
        var sceneMetadata = FindFirstObjectByType<EnvironmentCostInspectionMetadata>();
        if (sceneMetadata != null && sceneMetadata.GetComponent<EnvironmentCostRuntimePolicyScenarioController>() == null)
            sceneMetadata.gameObject.AddComponent<EnvironmentCostRuntimePolicyScenarioController>();
    }

    private IEnumerator InitializeWhenPackageReady()
    {
        metadata = GetComponent<EnvironmentCostInspectionMetadata>();
        packageLoader = GetComponent<EnvironmentCostRuntimeCityPackageLoader>();
        shadeAnalysis = GetComponent<EnvironmentCostRuntimeShadeAnalysisController>();
        while (packageLoader != null && (packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.NotStarted || packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.Loading)) yield return null;
        if (metadata == null || packageLoader == null || packageLoader.State != EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready)
        {
            status = "施策シナリオエディターには検証済みの都市データパッケージと検証シーン情報が必要です。";
            yield break;
        }
        CreateNewScenario();
        status = "種別を選び、道路または地表をクリックして配置します。既存の施策はクリックして選択し、ドラッグで移動できます。";
    }

    private void Update()
    {
        if (scenario == null || IsPointerOverPanel()) return;
        if (Input.GetMouseButtonDown(0)) BeginMapInteraction();
        if (dragging && Input.GetMouseButton(0)) MoveSelectedToRoad();
        if (dragging && Input.GetMouseButtonUp(0)) dragging = false;
        if (selected != null && (Input.GetKeyDown(KeyCode.Delete) || Input.GetKeyDown(KeyCode.Backspace))) DeleteSelected();
    }

    private void BeginMapInteraction()
    {
        // Selection intentionally tests only policy objects.  A CityGML building collider can be
        // in front of a visible road from an oblique camera angle, so it must not block placement.
        if (TryPolicyRaycast(out var policyHit, out var instance))
        {
            selected = instance.Facility;
            lastValidSelected = CloneFacility(selected);
            dragging = true;
            placeMode = false;
            status = $"「{selected.id}」を選択しました。道路または地表へドラッグして移動し、Delete キーで削除できます。";
            return;
        }
        if (!placeMode) return;
        if (TryGroundPosition(out var groundPosition, out var hasGroundCollider))
        {
            if (AddFacility(groundPosition) && !hasGroundCollider)
                status = "道路・地表 collider が見つからないため、ローカル地表基準面（Y=0）へ配置しました。必要に応じて位置を確認・調整してください。";
        }
        else status = "クリック位置を地表基準面へ投影できませんでした。";
    }

    private void MoveSelectedToRoad()
    {
        if (selected == null || !TryGroundPosition(out var groundPosition, out _)) return;
        if (!ValidatePosition(groundPosition, selected, out var issue)) { status = issue; return; }
        selected.localPosition = groundPosition;
        UpdateGeoCoordinate(selected);
        RenderScenario();
        lastValidSelected = CloneFacility(selected);
        MarkDirty("施策を移動しました。結果を更新するには日陰解析を再実行してください。");
    }

    private bool TryPolicyRaycast(out RaycastHit hit, out EnvironmentCostRuntimePolicyFacilityInstance instance)
    {
        hit = default;
        instance = null;
        var camera = ResolveInteractionCamera();
        if (camera == null) return false;
        // The Building layer contains both CityGML buildings and policy primitives.  Do not let a
        // static CityGML building count as a policy hit, nor hide a policy object behind it.
        foreach (var candidate in Physics.RaycastAll(camera.ScreenPointToRay(Input.mousePosition), 5000f,
                     1 << BuildingLayer, QueryTriggerInteraction.Ignore).OrderBy(candidate => candidate.distance))
        {
            var policyInstance = candidate.collider.GetComponentInParent<EnvironmentCostRuntimePolicyFacilityInstance>();
            if (policyInstance == null) continue;
            hit = candidate;
            instance = policyInstance;
            return true;
        }
        return false;
    }

    private bool TryGroundPosition(out Vector3 point, out bool hasGroundCollider)
    {
        point = default;
        hasGroundCollider = false;
        var camera = ResolveInteractionCamera();
        if (camera == null) return false;
        var mask = (1 << RoadLayer) | (1 << TerrainLayer);
        var ray = camera.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out var hit, 5000f, mask, QueryTriggerInteraction.Ignore))
        {
            point = hit.point;
            hasGroundCollider = true;
            return true;
        }

        // Some existing inspection Scenes contain a partial Road collider inventory even though
        // the CityGML road imagery is visible.  Keep those Scenes editable by using the same
        // local Y=0 fallback as the original Editor policy-facility generator.
        var groundPlane = new Plane(Vector3.up, Vector3.zero);
        if (!groundPlane.Raycast(ray, out var distance) || distance > 5000f) return false;
        point = ray.GetPoint(distance);
        return true;
    }

    private Camera ResolveInteractionCamera()
    {
        if (interactionCamera != null && interactionCamera.isActiveAndEnabled) return interactionCamera;
        interactionCamera = Camera.main;
        if (interactionCamera == null) interactionCamera = FindFirstObjectByType<Camera>();
        if (interactionCamera == null) status = "地図操作に使用できるカメラがありません。";
        return interactionCamera;
    }

    private bool ValidatePosition(Vector3 position, EnvironmentCostRuntimePolicyFacility excluded, out string issue)
    {
        issue = null;
        var horizontal = new Vector2(position.x, position.z);
        if (horizontal.magnitude > metadata.RadiusMeters) { issue = "配置位置が都市データパッケージの解析範囲外です。"; return false; }
        var groundStart = new Vector3(position.x, 1000f, position.z);
        var groundMask = (1 << RoadLayer) | (1 << TerrainLayer);
        var hasGround = Physics.Raycast(groundStart, Vector3.down, out var ground, 2000f, groundMask, QueryTriggerInteraction.Ignore);
        if (hasGround && Mathf.Abs(ground.point.y - position.y) > 0.25f)
        {
            issue = "配置位置が地表面から離れています。表示された地表高を入力するか、地図をクリックしてください。";
            return false;
        }
        if (!hasGround && Mathf.Abs(position.y) > 0.25f)
        {
            issue = "編集位置に道路・地表 collider がありません。ローカル地表基準面 Y=0 を使うか、地図をクリックしてください。";
            return false;
        }
        foreach (var facility in scenario.facilities)
        {
            if (facility == excluded) continue;
            var radius = FacilityFootprintRadius(facility) + FacilityFootprintRadius(excluded);
            if (Vector2.Distance(horizontal, new Vector2(facility.localPosition.x, facility.localPosition.z)) < radius)
            {
                issue = "配置位置が他の施策と重なっています。離れた位置へ移動してください。";
                return false;
            }
        }
        return true;
    }

    private bool AddFacility(Vector3 position)
    {
        var facility = new EnvironmentCostRuntimePolicyFacility
        {
            id = $"{selectedType}-{scenario.facilities.Count + 1:000}",
            type = selectedType,
            localPosition = position,
            heightMeters = selectedType == "obstacle" ? 3.0 : 6.0,
            radiusMeters = 1.8,
            widthMeters = 4.0,
            depthMeters = 4.0
        };
        if (!ValidatePosition(position, facility, out var issue)) { status = issue; return false; }
        UpdateGeoCoordinate(facility);
        scenario.facilities.Add(facility);
        selected = facility;
        lastValidSelected = CloneFacility(facility);
        RenderScenario();
        MarkDirty($"{FacilityTypeLabel(facility.type)}「{facility.id}」を追加しました。ドラッグまたは下の項目で編集できます。");
        return true;
    }

    private void DeleteSelected()
    {
        if (selected == null) return;
        var id = selected.id;
        scenario.facilities.Remove(selected);
        selected = null;
        lastValidSelected = null;
        RenderScenario();
        MarkDirty($"「{id}」を削除しました。");
    }

    private void CreateNewScenario()
    {
        scenario = new EnvironmentCostRuntimePolicyScenario
        {
            id = scenarioIdInput,
            displayName = displayNameInput,
            areaId = metadata.AreaId,
            coordinateZoneId = metadata.CoordinateZoneId,
            centerLongitude = metadata.Longitude,
            centerLatitude = metadata.Latitude,
            cityPackageVersion = packageLoader.Manifest.version,
            cityPackageManifestSha256 = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(Path.Combine(packageLoader.PackageRootPath, "manifest.json"))
        };
        selected = null;
        lastValidSelected = null;
        dirty = false;
        RenderScenario();
    }

    private void CloneScenario()
    {
        var clone = EnvironmentCostRuntimePolicyJson.Deserialize<EnvironmentCostRuntimePolicyScenario>(EnvironmentCostRuntimePolicyJson.Serialize(scenario));
        clone.id = scenario.id + "-copy";
        clone.displayName = scenario.displayName + "（コピー）";
        clone.createdAtUtc = null;
        clone.updatedAtUtc = null;
        scenario = clone;
        scenarioIdInput = clone.id;
        displayNameInput = clone.displayName;
        selected = null;
        lastValidSelected = null;
        RenderScenario();
        MarkDirty("シナリオを複製しました。IDを入力して保存してください。");
    }

    private void SaveScenario()
    {
        try
        {
            ApplyHeaderInputs();
            EnvironmentCostRuntimePolicyScenarioStore.Save(scenario);
            dirty = false;
            status = $"「{scenario.id}」を保存しました（{scenario.facilities.Count}件）。保存先: {EnvironmentCostRuntimePolicyScenarioStore.GetPath(scenario.areaId, scenario.id)}";
            Debug.Log($"ENVIRONMENT_COST_RUNTIME_POLICY_SCENARIO_SAVED area={scenario.areaId} id={scenario.id} facilities={scenario.facilities.Count} fingerprint={scenario.Fingerprint()}");
        }
        catch (Exception exception) { status = $"保存に失敗しました: {exception.Message}"; Debug.LogException(exception); }
    }

    private void LoadScenario(string path)
    {
        try
        {
            var loaded = EnvironmentCostRuntimePolicyScenarioStore.Load(path);
            ValidateLoadedScenarioPackage(loaded);
            scenario = loaded;
            scenarioIdInput = scenario.id; displayNameInput = scenario.displayName; authorInput = scenario.author; memoInput = scenario.evidenceMemo;
            selected = null; lastValidSelected = null; dirty = false; RenderScenario(); status = $"「{scenario.id}」を読み込みました。";
        }
        catch (Exception exception) { status = $"読込に失敗しました: {exception.Message}"; Debug.LogException(exception); }
    }

    private void ImportLegacyScenario()
    {
        try
        {
            var path = Path.Combine(Application.persistentDataPath, "EnvironmentCostScenarios", "import-policy-scenario.json");
            if (!File.Exists(path)) throw new FileNotFoundException("既存形式の 0.1 ポリシー JSON を次の場所へ置いてください", path);
            var legacy = JsonConvert.DeserializeObject<LegacyPolicyScenario>(File.ReadAllText(path)) ?? throw new InvalidOperationException("既存形式のポリシー JSON を読み取れませんでした。");
            if (legacy.schemaVersion != "environment-cost-policy-scenario-0.1") throw new InvalidOperationException("取り込めるのはポリシーシナリオのスキーマ 0.1 のみです。");
            CreateNewScenario(); scenario.id = legacy.id; scenario.displayName = legacy.id;
            var skipped = 0;
            foreach (var item in legacy.facilities ?? Array.Empty<LegacyPolicyFacility>())
            {
                if (item == null || (item.type != "tree" && item.type != "shade") || string.IsNullOrWhiteSpace(item.id) || scenario.facilities.Any(existing => existing.id == item.id)) { skipped++; continue; }
                var facility = new EnvironmentCostRuntimePolicyFacility { id = item.id, type = item.type, latitude = item.latitude, longitude = item.longitude, heightMeters = item.heightMeters, radiusMeters = item.radiusMeters, widthMeters = item.widthMeters, depthMeters = item.depthMeters };
                facility.localPosition = ToLocalPosition(item.latitude, item.longitude);
                try
                {
                    facility.Validate(item.id);
                    if (!ValidatePosition(facility.localPosition, facility, out var issue)) throw new InvalidOperationException(issue);
                }
                catch (Exception exception)
                {
                    skipped++;
                    Debug.LogWarning($"ENVIRONMENT_COST_RUNTIME_POLICY_LEGACY_FACILITY_SKIPPED id={item.id} reason={exception.Message}");
                    continue;
                }
                scenario.facilities.Add(facility);
            }
            scenarioIdInput = scenario.id; displayNameInput = scenario.displayName; RenderScenario(); MarkDirty($"既存形式の施策を{scenario.facilities.Count}件取り込みました。無効または未対応の施策は{skipped}件省略しました。Runtime シナリオとして保存してください。");
        }
        catch (Exception exception) { status = $"取込に失敗しました: {exception.Message}"; }
    }

    private void ApplyHeaderInputs()
    {
        scenario.id = scenarioIdInput.Trim(); scenario.displayName = displayNameInput.Trim(); scenario.author = authorInput.Trim(); scenario.evidenceMemo = memoInput.Trim();
    }

    private void ValidateLoadedScenarioPackage(EnvironmentCostRuntimePolicyScenario loaded)
    {
        var manifestPath = Path.Combine(packageLoader.PackageRootPath ?? throw new InvalidOperationException("検証済みの都市データパッケージのルートが利用できません。"), "manifest.json");
        var manifestSha = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(manifestPath);
        if (loaded.areaId != metadata.AreaId || loaded.coordinateZoneId != metadata.CoordinateZoneId ||
            Math.Abs(loaded.centerLongitude - metadata.Longitude) > 0.000001 || Math.Abs(loaded.centerLatitude - metadata.Latitude) > 0.000001 ||
            !string.Equals(loaded.cityPackageVersion, packageLoader.Manifest.version, StringComparison.Ordinal) ||
            !string.Equals(loaded.cityPackageManifestSha256, manifestSha, StringComparison.Ordinal))
            throw new InvalidOperationException("このシナリオは異なる都市データパッケージまたは座標系で保存されているため、このシーンには読み込めません。");
    }

    private static EnvironmentCostRuntimePolicyFacility CloneFacility(EnvironmentCostRuntimePolicyFacility source)
        => EnvironmentCostRuntimePolicyJson.Deserialize<EnvironmentCostRuntimePolicyFacility>(EnvironmentCostRuntimePolicyJson.Serialize(source));

    private void RestoreLastValidSelected()
    {
        if (selected == null || lastValidSelected == null) return;
        var index = scenario.facilities.IndexOf(selected);
        if (index < 0) return;
        scenario.facilities[index] = CloneFacility(lastValidSelected);
        selected = scenario.facilities[index];
    }

    private void UpdateGeoCoordinate(EnvironmentCostRuntimePolicyFacility facility)
    {
        using var local = CreateLocalReference();
        var coordinate = local.Unproject(new PlateauVector3d(facility.localPosition.x, facility.localPosition.y, facility.localPosition.z));
        facility.latitude = coordinate.Latitude; facility.longitude = coordinate.Longitude;
    }

    private Vector3 ToLocalPosition(double latitude, double longitude)
    {
        using var local = CreateLocalReference();
        var point = local.Project(new GeoCoordinate(latitude, longitude, 0.0));
        var rayStart = new Vector3((float)point.X, 1000f, (float)point.Z);
        return Physics.Raycast(rayStart, Vector3.down, out var hit, 2000f, (1 << RoadLayer) | (1 << TerrainLayer), QueryTriggerInteraction.Ignore)
            ? hit.point : new Vector3((float)point.X, 0f, (float)point.Z);
    }

    private GeoReference CreateLocalReference()
    {
        using var world = GeoReference.Create(new PlateauVector3d(0, 0, 0), 1f, CoordinateSystem.EUN, metadata.CoordinateZoneId);
        var reference = world.Project(new GeoCoordinate(metadata.Latitude, metadata.Longitude, 0.0));
        return GeoReference.Create(reference, 1f, CoordinateSystem.EUN, metadata.CoordinateZoneId);
    }

    private void RenderScenario()
    {
        if (scenarioRoot != null) Destroy(scenarioRoot.gameObject);
        scenarioRoot = new GameObject("RuntimePolicyScenario-" + (scenario?.id ?? "none")).transform;
        if (scenario == null) return;
        foreach (var facility in scenario.facilities) CreateFacilityVisual(facility);
        Physics.SyncTransforms();
    }

    private void CreateFacilityVisual(EnvironmentCostRuntimePolicyFacility facility)
    {
        var root = new GameObject($"Policy-{facility.type}-{facility.id}") { layer = BuildingLayer };
        root.transform.SetParent(scenarioRoot, false); root.transform.position = facility.localPosition; root.transform.rotation = Quaternion.Euler(0, facility.rotationDegrees, 0);
        root.AddComponent<EnvironmentCostRuntimePolicyFacilityInstance>().Facility = facility;
        if (facility.type == "tree")
        {
            var trunkHeight = Math.Max(1.0, facility.heightMeters * 0.55);
            CreatePrimitive(PrimitiveType.Cylinder, "trunk", root.transform, Vector3.up * (float)(trunkHeight / 2), new Vector3((float)facility.radiusMeters * .12f, (float)trunkHeight / 2, (float)facility.radiusMeters * .12f));
            var verticalRadius = Math.Clamp(facility.radiusMeters * .72, 1.2, 1.4);
            CreatePrimitive(PrimitiveType.Sphere, "canopy", root.transform, Vector3.up * (float)(facility.heightMeters - verticalRadius), new Vector3((float)facility.radiusMeters * 2, (float)verticalRadius * 2, (float)facility.radiusMeters * 2));
        }
        else if (facility.type == "shade")
        {
            CreatePrimitive(PrimitiveType.Cube, "roof", root.transform, Vector3.up * (float)(facility.heightMeters - .15), new Vector3((float)facility.widthMeters, .3f, (float)facility.depthMeters));
            foreach (var x in new[] { -.5f, .5f }) foreach (var z in new[] { -.5f, .5f })
                CreatePrimitive(PrimitiveType.Cylinder, "post", root.transform, new Vector3(x * (float)facility.widthMeters, (float)facility.heightMeters / 2, z * (float)facility.depthMeters), new Vector3(.12f, (float)facility.heightMeters / 2, .12f));
        }
        else CreatePrimitive(PrimitiveType.Cube, "obstacle", root.transform, Vector3.up * (float)facility.heightMeters / 2, new Vector3((float)facility.widthMeters, (float)facility.heightMeters, (float)facility.depthMeters));
    }

    private static void CreatePrimitive(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale)
    {
        var item = GameObject.CreatePrimitive(type); item.name = name; item.layer = BuildingLayer; item.transform.SetParent(parent, false); item.transform.localPosition = position; item.transform.localScale = scale;
    }

    private static float FacilityFootprintRadius(EnvironmentCostRuntimePolicyFacility facility) => facility == null ? 0f : facility.type == "tree" ? (float)facility.radiusMeters : Mathf.Max((float)facility.widthMeters, (float)facility.depthMeters) * .5f;
    private static bool IsGroundLayer(int layer) => layer == RoadLayer || layer == TerrainLayer;
    private static string FacilityTypeLabel(string type) => type == "tree" ? "樹木" : type == "shade" ? "日よけ" : "障害物";
    private bool IsPointerOverPanel() => Input.mousePosition.x < 490f && Input.mousePosition.y < 760f;
    private void MarkDirty(string message) { dirty = true; status = message; shadeAnalysis?.InvalidateForPolicyChange(scenario.id); }

    private void OnGUI()
    {
        if (!Application.isPlaying || scenario == null) return;
        GUILayout.BeginArea(new Rect(16, 324, 460, Mathf.Min(Screen.height - 340, 620)), GUI.skin.box);
        scroll = GUILayout.BeginScrollView(scroll);
        GUILayout.Label("施策シナリオエディター");
        GUILayout.Label(dirty ? "未保存の変更あり" : "保存済み");
        GUILayout.Label("シナリオ ID");
        scenarioIdInput = GUILayout.TextField(scenarioIdInput);
        GUILayout.Label("表示名");
        displayNameInput = GUILayout.TextField(displayNameInput);
        GUILayout.Label("担当者名");
        authorInput = GUILayout.TextField(authorInput);
        GUILayout.Label("根拠メモ");
        memoInput = GUILayout.TextArea(memoInput, GUILayout.MinHeight(36));
        GUILayout.BeginHorizontal(); foreach (var type in new[] { "tree", "shade", "obstacle" }) if (GUILayout.Toggle(selectedType == type, FacilityTypeLabel(type), "Button")) selectedType = type; GUILayout.EndHorizontal();
        placeMode = GUILayout.Toggle(placeMode, "選択した種別を道路・地表のクリックで配置", "Button");
        if (selected != null)
        {
            GUILayout.Label($"選択中: {selected.id}（{FacilityTypeLabel(selected.type)}）");
            selected.localPosition.x = FloatField("ローカル X（m）", selected.localPosition.x);
            selected.localPosition.y = FloatField("地表 Y（m）", selected.localPosition.y);
            selected.localPosition.z = FloatField("ローカル Z（m）", selected.localPosition.z);
            selected.heightMeters = DoubleField("高さ（m）", selected.heightMeters);
            if (selected.type == "tree") selected.radiusMeters = DoubleField("樹冠半径（m）", selected.radiusMeters); else { selected.widthMeters = DoubleField("幅（m）", selected.widthMeters); selected.depthMeters = DoubleField("奥行き（m）", selected.depthMeters); }
            selected.rotationDegrees = FloatField("向き（度）", selected.rotationDegrees);
            GUILayout.Label($"WGS84 座標: 緯度 {selected.latitude:F6}、経度 {selected.longitude:F6}");
            if (GUILayout.Button("位置・寸法を反映"))
            {
                try
                {
                    selected.Validate(selected.id);
                    if (!ValidatePosition(selected.localPosition, selected, out var issue)) throw new InvalidOperationException(issue);
                    UpdateGeoCoordinate(selected);
                    RenderScenario();
                    lastValidSelected = CloneFacility(selected);
                    MarkDirty("位置・寸法を更新しました。");
                }
                catch (Exception e) { RestoreLastValidSelected(); status = e.Message; }
            }
            if (GUILayout.Button("選択した施策を削除")) DeleteSelected();
        }
        GUILayout.BeginHorizontal(); if (GUILayout.Button("保存")) SaveScenario(); if (GUILayout.Button("A/B 比較用に複製")) CloneScenario(); if (GUILayout.Button("新規作成")) CreateNewScenario(); GUILayout.EndHorizontal();
        if (GUILayout.Button("既存 0.1 JSON を persistentDataPath から取り込む")) ImportLegacyScenario();
        foreach (var path in EnvironmentCostRuntimePolicyScenarioStore.List(metadata.AreaId)) if (GUILayout.Button("読み込む: " + Path.GetFileNameWithoutExtension(path))) LoadScenario(path);
        GUILayout.Label(status, GUILayout.ExpandHeight(true));
        GUILayout.EndScrollView(); GUILayout.EndArea();
    }

    private static double DoubleField(string label, double value)
    {
        GUILayout.BeginHorizontal(); GUILayout.Label(label, GUILayout.Width(150));
        var text = GUILayout.TextField(value.ToString("F2", CultureInfo.InvariantCulture)); GUILayout.EndHorizontal();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : value;
    }

    private static float FloatField(string label, float value)
    {
        GUILayout.BeginHorizontal(); GUILayout.Label(label, GUILayout.Width(150));
        var text = GUILayout.TextField(value.ToString("F1", CultureInfo.InvariantCulture)); GUILayout.EndHorizontal();
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : value;
    }

    [Serializable] private sealed class LegacyPolicyScenario { public string schemaVersion; public string id; public LegacyPolicyFacility[] facilities; }
    [Serializable] private sealed class LegacyPolicyFacility { public string id; public string type; public double latitude; public double longitude; public double heightMeters = 6; public double radiusMeters = 1.8; public double widthMeters = 4; public double depthMeters = 4; }
}

public sealed class EnvironmentCostRuntimePolicyFacilityInstance : MonoBehaviour
{
    public EnvironmentCostRuntimePolicyFacility Facility { get; set; }
}
