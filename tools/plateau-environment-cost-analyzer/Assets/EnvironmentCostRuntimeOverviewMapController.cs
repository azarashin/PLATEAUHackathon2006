using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PLATEAU.Geometries;
using PLATEAU.Native;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.UIElements;

/// <summary>
/// Renders a north-up, camera-following overview map for Runtime inspection Scenes.
/// The map range follows the main camera height without changing the main camera.
/// </summary>
public sealed class EnvironmentCostRuntimeOverviewMapController : MonoBehaviour
{
    private const int BuildingLayer = 8;
    private const int RoadLayer = 9;
    private const int TerrainLayer = 10;
    private const float DefaultMapExtentMeters = 500f;
    // The overview is deliberately closer than the package coverage so roads and shadows remain legible.
    private const float OverviewMapZoomMultiplier = 1.5f;
    private const float MarkerRotationUpdateThresholdDegrees = 0.1f;
    private static readonly Color PositionMarkerColor = new Color(13f / 255f, 148f / 255f, 136f / 255f, 1f);
    public const float MovingRefreshIntervalSeconds = 0.2f;
    public const float IdleRefreshIntervalSeconds = 1.0f;
    private static readonly ProfilerMarker OverviewMapRenderMarker = new ProfilerMarker("EnvironmentCost.OverviewMap.Render");

    private EnvironmentCostInspectionMetadata metadata;
    private Camera sourceCamera;
    private Camera overviewCamera;
    private RenderTexture renderTexture;
    private VisualElement mapContainer;
    private VisualElement positionMarker;
    private VisualElement placeLabelLayer;
    private Button visibilityButton;
    private Button placeLabelButton;
    private Label placeLabelStatus;
    private bool isVisible = true;
    private float nextRefreshTime;
    private Vector3 lastRenderedSourcePosition;
    private bool hasRendered;
    private float mapCameraY;
    private float mapExtentMeters;
    private float minimumSourceCameraHeight;
    private float maximumSourceCameraHeight;
    private bool hasSourceCameraHeightRange;
    private float lastMarkerRotationDegrees;
    private bool hasPositionMarkerRotation;
    private bool arePlaceLabelsVisible = true;
    private bool arePlaceLabelsLoaded;
    private bool placeLabelsDirty;
    private readonly List<RuntimePlaceLabel> placeLabels = new List<RuntimePlaceLabel>();
    private readonly Dictionary<string, Label> visiblePlaceLabels = new Dictionary<string, Label>();

    public bool IsPointerOverMap { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToLegacyInspectionScene()
    {
        var inspectionMetadata = FindFirstObjectByType<EnvironmentCostInspectionMetadata>();
        if (inspectionMetadata != null && inspectionMetadata.GetComponent<EnvironmentCostRuntimeOverviewMapController>() == null)
            inspectionMetadata.gameObject.AddComponent<EnvironmentCostRuntimeOverviewMapController>();
    }

    private IEnumerator Start()
    {
        metadata = GetComponent<EnvironmentCostInspectionMetadata>();
        while (ResolveSourceCamera() == null)
            yield return null;

        EnsureOverviewCamera();
        StartCoroutine(LoadPlaceLabelsWhenReady());
    }

    /// <summary>Builds the display-only overview map UI.</summary>
    public void BuildUi(VisualElement root)
    {
        metadata ??= GetComponent<EnvironmentCostInspectionMetadata>();
        var launcher = new VisualElement();
        launcher.AddToClassList("runtime-overview-map-launcher");
        launcher.RegisterCallback<PointerEnterEvent>(_ => SetPointerOverMap(true));
        launcher.RegisterCallback<PointerLeaveEvent>(_ => SetPointerOverMap(false));
        launcher.RegisterCallback<PointerDownEvent>(OnMapPointerDown);
        visibilityButton = new Button(ToggleVisibility);
        visibilityButton.AddToClassList("runtime-overview-map-toggle");
        launcher.Add(visibilityButton);
        root.Add(launcher);

        mapContainer = new VisualElement();
        mapContainer.AddToClassList("runtime-overview-map");
        mapContainer.RegisterCallback<PointerEnterEvent>(_ => SetPointerOverMap(true));
        mapContainer.RegisterCallback<PointerLeaveEvent>(_ => SetPointerOverMap(false));
        mapContainer.RegisterCallback<PointerDownEvent>(OnMapPointerDown);

        var title = new Label("俯瞰地図（北が上）");
        title.AddToClassList("runtime-overview-map-title");
        mapContainer.Add(title);
        placeLabelButton = new Button(TogglePlaceLabels);
        placeLabelButton.AddToClassList("runtime-overview-map-place-label-toggle");
        mapContainer.Add(placeLabelButton);
        var image = new Image { scaleMode = ScaleMode.ScaleToFit };
        image.AddToClassList("runtime-overview-map-image");
        mapContainer.Add(image);

        placeLabelLayer = new VisualElement { pickingMode = PickingMode.Ignore };
        placeLabelLayer.AddToClassList("runtime-overview-map-place-label-layer");
        image.Add(placeLabelLayer);

        positionMarker = new VisualElement { pickingMode = PickingMode.Ignore };
        positionMarker.AddToClassList("runtime-overview-map-position-marker");
        positionMarker.generateVisualContent += GeneratePositionMarkerVisualContent;
        positionMarker.tooltip = "現在地とメインカメラの向き";
        image.Add(positionMarker);
        positionMarker.MarkDirtyRepaint();

        placeLabelStatus = new Label("地名: 読み込み待ち");
        placeLabelStatus.AddToClassList("runtime-overview-map-place-label-status");
        mapContainer.Add(placeLabelStatus);

        root.Add(mapContainer);

        EnsureOverviewCamera();
        image.image = renderTexture;
        UpdateVisibilityUi();
        UpdatePlaceLabelUi();
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying || !isVisible || !EnsureOverviewCamera()) return;

        var sourcePosition = sourceCamera.transform.position;
        var extentChanged = UpdateMapExtentFromSourceCameraHeight(sourcePosition.y);
        UpdatePositionMarker();
        if (Time.unscaledTime < nextRefreshTime) return;

        var moved = !hasRendered || extentChanged || HorizontalDistanceSquared(sourcePosition, lastRenderedSourcePosition) > 0.25f;
        UpdateOverviewCameraTransform(sourcePosition);
        using (OverviewMapRenderMarker.Auto())
            overviewCamera.Render();
        lastRenderedSourcePosition = sourcePosition;
        hasRendered = true;
        if (moved || placeLabelsDirty) RefreshPlaceLabels(sourcePosition);
        nextRefreshTime = Time.unscaledTime + (moved ? MovingRefreshIntervalSeconds : IdleRefreshIntervalSeconds);
    }

    private bool EnsureOverviewCamera()
    {
        if (ResolveSourceCamera() == null) return false;

        if (overviewCamera != null && renderTexture != null) return true;

        if (overviewCamera == null)
        {
            var overviewObject = new GameObject("Environment Cost Runtime Overview Map Camera");
            overviewObject.transform.SetParent(transform, false);
            overviewCamera = overviewObject.AddComponent<Camera>();
        }
        overviewCamera.enabled = false; // Render manually at a throttled cadence.
        overviewCamera.orthographic = true;
        overviewCamera.clearFlags = CameraClearFlags.SolidColor;
        overviewCamera.backgroundColor = new Color(0.93f, 0.96f, 0.98f, 1f);
        overviewCamera.allowHDR = false;
        overviewCamera.allowMSAA = false;
        overviewCamera.cullingMask = CreateOverviewCullingMask(sourceCamera.cullingMask);
        EnsureSourceCameraHeightRange();
        UpdateMapExtentFromSourceCameraHeight(sourceCamera.transform.position.y);
        overviewCamera.orthographicSize = ResolveMapExtentMeters();
        overviewCamera.nearClipPlane = 0.1f;
        mapCameraY = ResolveMapCameraY();
        overviewCamera.farClipPlane = Mathf.Max(1000f, mapCameraY + 1000f);
        overviewCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        if (renderTexture == null)
        {
            renderTexture = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32)
            {
                name = "Environment Cost Runtime Overview Map",
                useMipMap = false,
                autoGenerateMips = false
            };
            renderTexture.Create();
        }
        overviewCamera.targetTexture = renderTexture;
        return true;
    }

    private Camera ResolveSourceCamera()
    {
        if (IsUsableSourceCamera(sourceCamera, overviewCamera)) return sourceCamera;
        sourceCamera = null;
        sourceCamera = Camera.main;
        if (!IsUsableSourceCamera(sourceCamera, overviewCamera))
        {
            sourceCamera = null;
            foreach (var candidate in FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (IsUsableSourceCamera(candidate, overviewCamera)) { sourceCamera = candidate; break; }
            }
        }
        return sourceCamera;
    }

    /// <summary>Rejects destroyed, disabled, and self-rendering cameras when choosing the main view.</summary>
    public static bool IsUsableSourceCamera(Camera candidate, Camera overview)
        => candidate != null && candidate != overview && candidate.isActiveAndEnabled;

    /// <summary>Smallest map radius for a city package. The visible square is twice this value.</summary>
    public static float GetMinimumMapExtentMeters(float packageRadiusMeters)
    {
        return Mathf.Min(200f, GetPackageCoverageRadiusMeters(packageRadiusMeters)) / OverviewMapZoomMultiplier;
    }

    /// <summary>Largest display radius, limited to the generated city package coverage.</summary>
    public static float GetMaximumMapExtentMeters(float packageRadiusMeters)
        => GetPackageCoverageRadiusMeters(packageRadiusMeters) / OverviewMapZoomMultiplier;

    /// <summary>Clamps a map radius to the range supported by the current city package.</summary>
    public static float ClampMapExtentMeters(float requestedMeters, float packageRadiusMeters)
        => Mathf.Clamp(requestedMeters, GetMinimumMapExtentMeters(packageRadiusMeters), GetMaximumMapExtentMeters(packageRadiusMeters));

    private float GetPackageRadiusMeters() => metadata == null ? 0f : metadata.RadiusMeters;

    /// <summary>Uses the lowest scene geometry and the main camera's configured near clip as the closest valid overview height.</summary>
    public static float GetMinimumSourceCameraHeightMeters(float sceneMinimumY, float sourceCameraNearClipPlane)
        => sceneMinimumY + Mathf.Max(0.1f, sourceCameraNearClipPlane);

    /// <summary>Uses one city-package radius above the lowest scene geometry as the farthest valid overview height.</summary>
    public static float GetMaximumSourceCameraHeightMeters(float sceneMinimumY, float packageRadiusMeters)
        => sceneMinimumY + GetPackageCoverageRadiusMeters(packageRadiusMeters);

    /// <summary>Maps a valid source-camera height linearly to the supported overview-map radius.</summary>
    public static float GetMapExtentMetersForSourceCameraHeight(float sourceCameraHeight, float minimumSourceCameraHeight,
        float maximumSourceCameraHeight, float packageRadiusMeters)
    {
        var normalizedHeight = Mathf.InverseLerp(minimumSourceCameraHeight, maximumSourceCameraHeight, sourceCameraHeight);
        return Mathf.Lerp(GetMinimumMapExtentMeters(packageRadiusMeters), GetMaximumMapExtentMeters(packageRadiusMeters), normalizedHeight);
    }

    private float ResolveMapExtentMeters() => ClampMapExtentMeters(mapExtentMeters, GetPackageRadiusMeters());

    private static float GetPackageCoverageRadiusMeters(float packageRadiusMeters)
        => packageRadiusMeters > 0f ? packageRadiusMeters : DefaultMapExtentMeters;

    private void EnsureSourceCameraHeightRange()
    {
        if (hasSourceCameraHeightRange || sourceCamera == null) return;
        var sceneMinimumY = ResolveSceneMinimumY();
        minimumSourceCameraHeight = GetMinimumSourceCameraHeightMeters(sceneMinimumY, sourceCamera.nearClipPlane);
        maximumSourceCameraHeight = GetMaximumSourceCameraHeightMeters(sceneMinimumY, GetPackageRadiusMeters());
        hasSourceCameraHeightRange = true;
    }

    private float ResolveSceneMinimumY()
    {
        var minimum = 0f;
        var hasRenderer = false;
        var currentScene = gameObject.scene;
        foreach (var renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (renderer.gameObject.scene != currentScene) continue;
            minimum = hasRenderer ? Mathf.Min(minimum, renderer.bounds.min.y) : renderer.bounds.min.y;
            hasRenderer = true;
        }
        return minimum;
    }

    private float ResolveMapCameraY()
    {
        var highest = 100f;
        var currentScene = gameObject.scene;
        foreach (var renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (renderer.gameObject.scene == currentScene)
                highest = Mathf.Max(highest, renderer.bounds.max.y);
        }
        return highest + Mathf.Max(500f, ResolveMapExtentMeters() * 0.25f);
    }

    private void UpdateOverviewCameraTransform(Vector3 sourcePosition)
    {
        overviewCamera.transform.position = new Vector3(sourcePosition.x, mapCameraY, sourcePosition.z);
        // Do not copy source rotation: the overview map remains north-up by design.
        overviewCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    private bool UpdateMapExtentFromSourceCameraHeight(float sourceCameraHeight)
    {
        EnsureSourceCameraHeightRange();
        var nextExtentMeters = GetMapExtentMetersForSourceCameraHeight(sourceCameraHeight, minimumSourceCameraHeight,
            maximumSourceCameraHeight, GetPackageRadiusMeters());
        if (Mathf.Approximately(mapExtentMeters, nextExtentMeters)) return false;

        mapExtentMeters = nextExtentMeters;
        if (overviewCamera != null)
        {
            overviewCamera.orthographicSize = mapExtentMeters;
            mapCameraY = ResolveMapCameraY();
            overviewCamera.farClipPlane = Mathf.Max(1000f, mapCameraY + 1000f);
        }
        return true;
    }

    private void UpdatePositionMarker()
    {
        if (positionMarker == null || sourceCamera == null) return;
        // North is the top of the image. A source yaw of zero looks north (+Z), so use yaw directly.
        var rotationDegrees = GetPositionMarkerRotationDegrees(sourceCamera.transform.rotation);
        if (!ShouldUpdatePositionMarkerRotation(hasPositionMarkerRotation, lastMarkerRotationDegrees, rotationDegrees)) return;
        positionMarker.style.rotate = new Rotate(new Angle(rotationDegrees, AngleUnit.Degree));
        lastMarkerRotationDegrees = rotationDegrees;
        hasPositionMarkerRotation = true;
    }

    /// <summary>Returns the clockwise north-up marker rotation for a source-camera rotation.</summary>
    public static float GetPositionMarkerRotationDegrees(Quaternion sourceRotation)
        => sourceRotation.eulerAngles.y;

    /// <summary>Skips a retained-mode style update until the north-up marker rotation visibly changes.</summary>
    public static bool ShouldUpdatePositionMarkerRotation(bool hasPreviousRotation, float previousDegrees, float nextDegrees)
        => !hasPreviousRotation || Mathf.Abs(Mathf.DeltaAngle(previousDegrees, nextDegrees)) >= MarkerRotationUpdateThresholdDegrees;

    /// <summary>Returns an upward-pointing isosceles triangle for the north-up camera marker.</summary>
    public static void GetPositionMarkerTriangleVertices(float width, float height, out Vector2 tip, out Vector2 leftBase, out Vector2 rightBase)
    {
        tip = new Vector2(width * 0.5f, 0f);
        leftBase = new Vector2(0f, height);
        rightBase = new Vector2(width, height);
    }

    private static void GeneratePositionMarkerVisualContent(MeshGenerationContext context)
    {
        var rect = context.visualElement.contentRect;
        if (rect.width <= 0f || rect.height <= 0f) return;

        GetPositionMarkerTriangleVertices(rect.width, rect.height, out var tip, out var leftBase, out var rightBase);
        var mesh = context.Allocate(3, 3);
        mesh.SetNextVertex(new Vertex { position = new Vector3(tip.x, tip.y, Vertex.nearZ), tint = PositionMarkerColor });
        mesh.SetNextVertex(new Vertex { position = new Vector3(leftBase.x, leftBase.y, Vertex.nearZ), tint = PositionMarkerColor });
        mesh.SetNextVertex(new Vertex { position = new Vector3(rightBase.x, rightBase.y, Vertex.nearZ), tint = PositionMarkerColor });
        // UI Toolkit culls back-facing generated meshes. This winding faces the panel camera.
        mesh.SetNextIndex(0);
        mesh.SetNextIndex(2);
        mesh.SetNextIndex(1);
    }

    private IEnumerator LoadPlaceLabelsWhenReady()
    {
        var packageLoader = GetComponent<EnvironmentCostRuntimeCityPackageLoader>();
        while (packageLoader == null)
        {
            packageLoader = GetComponent<EnvironmentCostRuntimeCityPackageLoader>();
            yield return null;
        }
        while (packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.NotStarted ||
               packageLoader.State == EnvironmentCostRuntimeCityPackageLoader.PackageState.Loading)
            yield return null;

        if (packageLoader.State != EnvironmentCostRuntimeCityPackageLoader.PackageState.Ready)
        {
            SetPlaceLabelStatus("地名: 都市データパッケージを確認できません", false);
            yield break;
        }

        try
        {
            var manifest = packageLoader.Manifest;
            if (!string.Equals(manifest.schemaVersion, "environment-cost-runtime-city-package-0.2", System.StringComparison.Ordinal))
            {
                SetPlaceLabelStatus("地名: この都市データパッケージは地名表示に未対応です", false);
                yield break;
            }

            var root = packageLoader.PackageRootPath;
            var labels = JsonUtility.FromJson<EnvironmentCostPlaceLabels>(File.ReadAllText(ResolvePackageFilePath(manifest, root, "place-labels")));
            var report = JsonUtility.FromJson<EnvironmentCostPlaceLabelReport>(File.ReadAllText(ResolvePackageFilePath(manifest, root, "place-label-report")));
            if (labels == null || report == null) throw new System.InvalidOperationException("地名データを読み取れません。");
            LoadPlaceLabels(labels);
            SetPlaceLabelStatus(BuildPlaceLabelStatus(manifest, report), labels.labels != null && labels.labels.Length > 0);
        }
        catch (System.Exception exception)
        {
            UnityEngine.Debug.LogException(exception);
            SetPlaceLabelStatus("地名: 読み込みに失敗しました", false);
        }
    }

    private static string ResolvePackageFilePath(EnvironmentCostRuntimeCityPackageManifest manifest, string root, string kind)
    {
        var file = manifest.files.FirstOrDefault(candidate => string.Equals(candidate.kind, kind, System.StringComparison.Ordinal));
        if (file == null) throw new System.InvalidOperationException("都市データパッケージに必要な地名ファイルがありません: " + kind);
        return Path.Combine(root, file.relativePath);
    }

    private void LoadPlaceLabels(EnvironmentCostPlaceLabels labels)
    {
        placeLabels.Clear();
        if (labels.labels != null && metadata != null)
        {
            using var reference = CreateLocalReference();
            foreach (var label in labels.labels)
            {
                if (label == null || string.IsNullOrWhiteSpace(label.id) || string.IsNullOrWhiteSpace(label.text) ||
                    label.coordinate == null || label.coordinate.Length != 2) continue;
                var point = reference.Project(new GeoCoordinate(label.coordinate[1], label.coordinate[0], 0.0));
                placeLabels.Add(new RuntimePlaceLabel { id = label.id, text = label.text, priority = label.priority,
                    localPosition = new Vector2((float)point.X, (float)point.Z) });
            }
        }
        placeLabels.Sort((left, right) =>
        {
            var priority = right.priority.CompareTo(left.priority);
            return priority != 0 ? priority : string.CompareOrdinal(left.id, right.id);
        });
        arePlaceLabelsLoaded = true;
        placeLabelsDirty = true;
    }

    private GeoReference CreateLocalReference()
    {
        using var world = GeoReference.Create(new PlateauVector3d(0, 0, 0), 1f, CoordinateSystem.EUN, metadata.CoordinateZoneId);
        var origin = world.Project(new GeoCoordinate(metadata.Latitude, metadata.Longitude, 0.0));
        return GeoReference.Create(origin, 1f, CoordinateSystem.EUN, metadata.CoordinateZoneId);
    }

    private void RefreshPlaceLabels(Vector3 sourcePosition)
    {
        if (!arePlaceLabelsLoaded || placeLabelLayer == null) return;
        var mapSize = placeLabelLayer.contentRect.size;
        if (mapSize.x <= 0f || mapSize.y <= 0f) return;
        placeLabelsDirty = false;
        var needed = new HashSet<string>();
        if (arePlaceLabelsVisible)
        {
            var occupied = new List<Rect> { GetPositionMarkerRect(mapSize) };
            var minimumPriority = GetMinimumPlaceLabelPriority(mapExtentMeters);
            var maximumCount = GetMaximumVisiblePlaceLabelCount(mapExtentMeters);
            foreach (var placeLabel in placeLabels)
            {
                if (needed.Count >= maximumCount) break;
                if (placeLabel.priority < minimumPriority ||
                    !TryGetPlaceLabelRect(placeLabel.localPosition, sourcePosition, mapExtentMeters, mapSize, placeLabel.text, out var rect) ||
                    occupied.Any(existing => existing.Overlaps(rect))) continue;
                occupied.Add(rect);
                needed.Add(placeLabel.id);
                if (!visiblePlaceLabels.TryGetValue(placeLabel.id, out var element))
                {
                    element = new Label(placeLabel.text) { pickingMode = PickingMode.Ignore };
                    element.AddToClassList("runtime-overview-map-place-label");
                    placeLabelLayer.Add(element);
                    visiblePlaceLabels.Add(placeLabel.id, element);
                }
                element.style.left = rect.x;
                element.style.top = rect.y;
                element.style.width = rect.width;
                element.style.height = rect.height;
            }
        }
        foreach (var stale in visiblePlaceLabels.Keys.Where(id => !needed.Contains(id)).ToArray())
        {
            visiblePlaceLabels[stale].RemoveFromHierarchy();
            visiblePlaceLabels.Remove(stale);
        }
    }

    /// <summary>Projects a local X/Z coordinate into the north-up overview image and clips its label rectangle.</summary>
    public static bool TryGetPlaceLabelRect(Vector2 localPosition, Vector3 sourcePosition, float mapExtentMeters, Vector2 mapSize,
        string text, out Rect rect)
    {
        rect = default;
        if (mapExtentMeters <= 0f || mapSize.x <= 0f || mapSize.y <= 0f || string.IsNullOrWhiteSpace(text)) return false;
        var center = new Vector2(mapSize.x * 0.5f, mapSize.y * 0.5f);
        var point = center + new Vector2((localPosition.x - sourcePosition.x) / mapExtentMeters * center.x,
            -(localPosition.y - sourcePosition.z) / mapExtentMeters * center.y);
        var width = Mathf.Clamp(text.Length * 9f + 10f, 34f, 118f);
        rect = new Rect(point.x - width * 0.5f, point.y - 8f, width, 16f);
        return rect.xMin >= 0f && rect.yMin >= 0f && rect.xMax <= mapSize.x && rect.yMax <= mapSize.y;
    }

    /// <summary>Uses fewer labels and higher-priority labels as the overview covers a wider area.</summary>
    public static int GetMinimumPlaceLabelPriority(float mapExtentMeters)
        => mapExtentMeters <= 160f ? 60 : mapExtentMeters <= 350f ? 70 : 80;

    /// <summary>Caps label count per scale so labels remain readable instead of covering the map.</summary>
    public static int GetMaximumVisiblePlaceLabelCount(float mapExtentMeters)
        => mapExtentMeters <= 160f ? 12 : mapExtentMeters <= 350f ? 10 : 8;

    private static Rect GetPositionMarkerRect(Vector2 mapSize) => new Rect(mapSize.x * 0.5f - 11f, mapSize.y * 0.5f - 14f, 22f, 28f);

    private static string BuildPlaceLabelStatus(EnvironmentCostRuntimeCityPackageManifest manifest, EnvironmentCostPlaceLabelReport report)
    {
        if (report.labelCount <= 0)
            return "地名: 不足（" + FormatPlaceLabelReasons(report.reasonCodes) + "）";
        var acquisition = report.acquisitionSources != null && report.acquisitionSources.Length > 0 ? report.acquisitionSources[0] : null;
        var source = acquisition == null ? "出典: PLATEAU" : $"出典: {acquisition.provider} {acquisition.year}";
        var version = string.IsNullOrWhiteSpace(report.sourceVersion) ? manifest.version : report.sourceVersion;
        var warning = report.reasonCodes == null || report.reasonCodes.Length == 0 ? string.Empty : " / 注意: " + FormatPlaceLabelReasons(report.reasonCodes);
        return $"地名: {report.labelCount}件 / {source} / 版: {version}{warning}";
    }

    private static string FormatPlaceLabelReasons(string[] reasonCodes)
    {
        if (reasonCodes == null || reasonCodes.Length == 0) return "地名データなし";
        return string.Join("、", reasonCodes.Select(reason => reason switch
        {
            "citygml-source-not-found" => "CityGML未配置",
            "citygml-acquisition-manifest-missing" => "取得台帳なし",
            "citygml-parse-errors" => "CityGML読込エラー",
            "no-place-labels-extracted" => "抽出対象なし",
            _ => reason
        }));
    }

    private void TogglePlaceLabels()
    {
        arePlaceLabelsVisible = !arePlaceLabelsVisible;
        placeLabelsDirty = true;
        UpdatePlaceLabelUi();
    }

    private void SetPlaceLabelStatus(string text, bool loaded)
    {
        arePlaceLabelsLoaded = loaded;
        if (placeLabelStatus != null) placeLabelStatus.text = text;
        UpdatePlaceLabelUi();
    }

    private void UpdatePlaceLabelUi()
    {
        if (placeLabelButton == null) return;
        placeLabelButton.text = arePlaceLabelsVisible ? "地名を非表示" : "地名を表示";
        placeLabelButton.SetEnabled(arePlaceLabelsLoaded);
    }

    private sealed class RuntimePlaceLabel
    {
        public string id;
        public string text;
        public int priority;
        public Vector2 localPosition;
    }

    private void ToggleVisibility()
    {
        isVisible = !isVisible;
        if (isVisible) { hasRendered = false; nextRefreshTime = 0f; }
        UpdateVisibilityUi();
    }

    private void UpdateVisibilityUi()
    {
        if (mapContainer != null) mapContainer.style.display = isVisible ? DisplayStyle.Flex : DisplayStyle.None;
        if (visibilityButton != null) visibilityButton.text = isVisible ? "俯瞰地図を隠す" : "俯瞰地図を表示";
    }

    private void SetPointerOverMap(bool value)
    {
        IsPointerOverMap = value;
        EnvironmentCostRuntimeUiInputGate.SetAdditionalPointerOverUi(value);
    }

    private void OnMapPointerDown(PointerDownEvent evt)
    {
        EnvironmentCostRuntimeUiInputGate.HandlePointerSelection(null, true);
        evt.StopPropagation();
    }

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right)
    {
        var x = left.x - right.x;
        var z = left.z - right.z;
        return x * x + z * z;
    }

    /// <summary>Includes only the permanent city-model layers required by the overview map.</summary>
    public static int CreateOverviewCullingMask(int sourceMask)
        => sourceMask & ((1 << BuildingLayer) | (1 << RoadLayer) | (1 << TerrainLayer));

    private void OnDestroy()
    {
        SetPointerOverMap(false);
        if (overviewCamera != null) Destroy(overviewCamera.gameObject);
        if (renderTexture == null) return;
        renderTexture.Release();
        Destroy(renderTexture);
    }
}
