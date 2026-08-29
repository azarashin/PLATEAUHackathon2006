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
    private string status = "Loading Runtime policy editor…";
    private string selectedType = "tree";
    private string scenarioIdInput = "runtime-scenario";
    private string displayNameInput = "New scenario";
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
            status = "Runtime policy editor requires a verified city package and inspection metadata.";
            yield break;
        }
        CreateNewScenario();
        status = "Select a type, then place it on a road. Click an existing policy object to select and drag it.";
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
            status = $"Selected {selected.id}. Drag on a road to move; Delete removes it.";
            return;
        }
        if (!placeMode) return;
        if (TryGroundPosition(out var groundPosition, out var hasGroundCollider))
        {
            AddFacility(groundPosition);
            if (!hasGroundCollider) status = "No Road/Terrain collider was found; placed on the local ground reference plane (Y=0). Verify and adjust the position if needed.";
        }
        else status = "The map click could not be projected onto the ground reference plane.";
    }

    private void MoveSelectedToRoad()
    {
        if (selected == null || !TryGroundPosition(out var groundPosition, out _)) return;
        if (!ValidatePosition(groundPosition, selected, out var issue)) { status = issue; return; }
        selected.localPosition = groundPosition;
        UpdateGeoCoordinate(selected);
        RenderScenario();
        lastValidSelected = CloneFacility(selected);
        MarkDirty("Facility moved. Re-run analysis to refresh the in-memory result.");
    }

    private bool TryPolicyRaycast(out RaycastHit hit, out EnvironmentCostRuntimePolicyFacilityInstance instance)
    {
        hit = default;
        instance = null;
        var camera = Camera.main;
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
        var camera = Camera.main;
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

    private bool ValidatePosition(Vector3 position, EnvironmentCostRuntimePolicyFacility excluded, out string issue)
    {
        issue = null;
        var horizontal = new Vector2(position.x, position.z);
        if (horizontal.magnitude > metadata.RadiusMeters) { issue = "Placement is outside the packaged analysis extent."; return false; }
        var groundStart = new Vector3(position.x, 1000f, position.z);
        var groundMask = (1 << RoadLayer) | (1 << TerrainLayer);
        var hasGround = Physics.Raycast(groundStart, Vector3.down, out var ground, 2000f, groundMask, QueryTriggerInteraction.Ignore);
        if (hasGround && Mathf.Abs(ground.point.y - position.y) > 0.25f)
        {
            issue = "Placement is off the ground surface. Use the displayed ground height or click the map.";
            return false;
        }
        if (!hasGround && Mathf.Abs(position.y) > 0.25f)
        {
            issue = "This Scene has no Road/Terrain collider at the edited position; use local ground reference Y=0 or click the map.";
            return false;
        }
        foreach (var facility in scenario.facilities)
        {
            if (facility == excluded) continue;
            var radius = FacilityFootprintRadius(facility) + FacilityFootprintRadius(excluded);
            if (Vector2.Distance(horizontal, new Vector2(facility.localPosition.x, facility.localPosition.z)) < radius)
            {
                issue = "Placement overlaps another policy facility. Move it further away.";
                return false;
            }
        }
        return true;
    }

    private void AddFacility(Vector3 position)
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
        if (!ValidatePosition(position, facility, out var issue)) { status = issue; return; }
        UpdateGeoCoordinate(facility);
        scenario.facilities.Add(facility);
        selected = facility;
        lastValidSelected = CloneFacility(facility);
        RenderScenario();
        MarkDirty($"Added {facility.type} {facility.id}. Drag it or edit its values below.");
    }

    private void DeleteSelected()
    {
        if (selected == null) return;
        var id = selected.id;
        scenario.facilities.Remove(selected);
        selected = null;
        lastValidSelected = null;
        RenderScenario();
        MarkDirty($"Deleted {id}.");
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
        var clone = JsonConvert.DeserializeObject<EnvironmentCostRuntimePolicyScenario>(JsonConvert.SerializeObject(scenario));
        clone.id = scenario.id + "-copy";
        clone.displayName = scenario.displayName + " copy";
        clone.createdAtUtc = null;
        clone.updatedAtUtc = null;
        scenario = clone;
        scenarioIdInput = clone.id;
        displayNameInput = clone.displayName;
        selected = null;
        lastValidSelected = null;
        RenderScenario();
        MarkDirty("Scenario cloned. Give it an ID and save it.");
    }

    private void SaveScenario()
    {
        try
        {
            ApplyHeaderInputs();
            EnvironmentCostRuntimePolicyScenarioStore.Save(scenario);
            dirty = false;
            status = $"Saved {scenario.id} ({scenario.facilities.Count} facilities) to {EnvironmentCostRuntimePolicyScenarioStore.GetPath(scenario.areaId, scenario.id)}";
            Debug.Log($"ENVIRONMENT_COST_RUNTIME_POLICY_SCENARIO_SAVED area={scenario.areaId} id={scenario.id} facilities={scenario.facilities.Count} fingerprint={scenario.Fingerprint()}");
        }
        catch (Exception exception) { status = $"Save failed: {exception.Message}"; Debug.LogException(exception); }
    }

    private void LoadScenario(string path)
    {
        try
        {
            var loaded = EnvironmentCostRuntimePolicyScenarioStore.Load(path);
            ValidateLoadedScenarioPackage(loaded);
            scenario = loaded;
            scenarioIdInput = scenario.id; displayNameInput = scenario.displayName; authorInput = scenario.author; memoInput = scenario.evidenceMemo;
            selected = null; lastValidSelected = null; dirty = false; RenderScenario(); status = $"Loaded {scenario.id}.";
        }
        catch (Exception exception) { status = $"Load failed: {exception.Message}"; Debug.LogException(exception); }
    }

    private void ImportLegacyScenario()
    {
        try
        {
            var path = Path.Combine(Application.persistentDataPath, "EnvironmentCostScenarios", "import-policy-scenario.json");
            if (!File.Exists(path)) throw new FileNotFoundException("Put an existing 0.1 policy JSON at", path);
            var legacy = JsonConvert.DeserializeObject<LegacyPolicyScenario>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Legacy policy JSON could not be parsed.");
            if (legacy.schemaVersion != "environment-cost-policy-scenario-0.1") throw new InvalidOperationException("Only policy scenario schema 0.1 can be imported.");
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
            scenarioIdInput = scenario.id; displayNameInput = scenario.displayName; RenderScenario(); MarkDirty($"Imported {scenario.facilities.Count} legacy facilities; skipped {skipped} invalid or unsupported facilities. Save as a Runtime scenario.");
        }
        catch (Exception exception) { status = $"Import failed: {exception.Message}"; }
    }

    private void ApplyHeaderInputs()
    {
        scenario.id = scenarioIdInput.Trim(); scenario.displayName = displayNameInput.Trim(); scenario.author = authorInput.Trim(); scenario.evidenceMemo = memoInput.Trim();
    }

    private void ValidateLoadedScenarioPackage(EnvironmentCostRuntimePolicyScenario loaded)
    {
        var manifestPath = Path.Combine(packageLoader.PackageRootPath ?? throw new InvalidOperationException("Verified package root is unavailable."), "manifest.json");
        var manifestSha = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(manifestPath);
        if (loaded.areaId != metadata.AreaId || loaded.coordinateZoneId != metadata.CoordinateZoneId ||
            Math.Abs(loaded.centerLongitude - metadata.Longitude) > 0.000001 || Math.Abs(loaded.centerLatitude - metadata.Latitude) > 0.000001 ||
            !string.Equals(loaded.cityPackageVersion, packageLoader.Manifest.version, StringComparison.Ordinal) ||
            !string.Equals(loaded.cityPackageManifestSha256, manifestSha, StringComparison.Ordinal))
            throw new InvalidOperationException("Scenario was saved for a different city package or coordinate reference and cannot be loaded into this scene.");
    }

    private static EnvironmentCostRuntimePolicyFacility CloneFacility(EnvironmentCostRuntimePolicyFacility source)
        => JsonConvert.DeserializeObject<EnvironmentCostRuntimePolicyFacility>(JsonConvert.SerializeObject(source));

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
    private bool IsPointerOverPanel() => Input.mousePosition.x < 490f && Input.mousePosition.y < 760f;
    private void MarkDirty(string message) { dirty = true; status = message; shadeAnalysis?.InvalidateForPolicyChange(scenario.id); }

    private void OnGUI()
    {
        if (!Application.isPlaying || scenario == null) return;
        GUILayout.BeginArea(new Rect(16, 324, 460, Mathf.Min(Screen.height - 340, 620)), GUI.skin.box);
        scroll = GUILayout.BeginScrollView(scroll);
        GUILayout.Label("Runtime Policy Scenario Editor");
        GUILayout.Label(dirty ? "Unsaved changes" : "Saved state");
        scenarioIdInput = GUILayout.TextField(scenarioIdInput); displayNameInput = GUILayout.TextField(displayNameInput); authorInput = GUILayout.TextField(authorInput); memoInput = GUILayout.TextArea(memoInput, GUILayout.MinHeight(36));
        GUILayout.BeginHorizontal(); foreach (var type in new[] { "tree", "shade", "obstacle" }) if (GUILayout.Toggle(selectedType == type, type, "Button")) selectedType = type; GUILayout.EndHorizontal();
        placeMode = GUILayout.Toggle(placeMode, "Place selected type by clicking Road / Terrain", "Button");
        if (selected != null)
        {
            GUILayout.Label($"Selected: {selected.id} ({selected.type})");
            selected.localPosition.x = FloatField("Local X m", selected.localPosition.x);
            selected.localPosition.y = FloatField("Ground Y m", selected.localPosition.y);
            selected.localPosition.z = FloatField("Local Z m", selected.localPosition.z);
            selected.heightMeters = DoubleField("Height m", selected.heightMeters);
            if (selected.type == "tree") selected.radiusMeters = DoubleField("Canopy radius m", selected.radiusMeters); else { selected.widthMeters = DoubleField("Width m", selected.widthMeters); selected.depthMeters = DoubleField("Depth m", selected.depthMeters); }
            selected.rotationDegrees = FloatField("Direction deg", selected.rotationDegrees);
            GUILayout.Label($"WGS84: {selected.latitude:F6}, {selected.longitude:F6}");
            if (GUILayout.Button("Apply selected position / dimensions"))
            {
                try
                {
                    selected.Validate(selected.id);
                    if (!ValidatePosition(selected.localPosition, selected, out var issue)) throw new InvalidOperationException(issue);
                    UpdateGeoCoordinate(selected);
                    RenderScenario();
                    lastValidSelected = CloneFacility(selected);
                    MarkDirty("Position and dimensions updated.");
                }
                catch (Exception e) { RestoreLastValidSelected(); status = e.Message; }
            }
            if (GUILayout.Button("Delete selected")) DeleteSelected();
        }
        GUILayout.BeginHorizontal(); if (GUILayout.Button("Save")) SaveScenario(); if (GUILayout.Button("Clone A/B")) CloneScenario(); if (GUILayout.Button("New")) CreateNewScenario(); GUILayout.EndHorizontal();
        if (GUILayout.Button("Import existing 0.1 JSON from persistentDataPath")) ImportLegacyScenario();
        foreach (var path in EnvironmentCostRuntimePolicyScenarioStore.List(metadata.AreaId)) if (GUILayout.Button("Load " + Path.GetFileNameWithoutExtension(path))) LoadScenario(path);
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
