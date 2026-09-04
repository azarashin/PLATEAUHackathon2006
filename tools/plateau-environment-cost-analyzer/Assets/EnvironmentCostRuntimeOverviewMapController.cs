using System.Collections;
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
    private Button visibilityButton;
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
        var image = new Image { scaleMode = ScaleMode.ScaleToFit };
        image.AddToClassList("runtime-overview-map-image");
        mapContainer.Add(image);

        positionMarker = new VisualElement { pickingMode = PickingMode.Ignore };
        positionMarker.AddToClassList("runtime-overview-map-position-marker");
        positionMarker.generateVisualContent += GeneratePositionMarkerVisualContent;
        positionMarker.tooltip = "現在地とメインカメラの向き";
        image.Add(positionMarker);

        root.Add(mapContainer);

        EnsureOverviewCamera();
        image.image = renderTexture;
        UpdateVisibilityUi();
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
        var maximum = GetMaximumMapExtentMeters(packageRadiusMeters);
        return Mathf.Min(200f, maximum);
    }

    /// <summary>Largest display radius, limited to the generated city package coverage.</summary>
    public static float GetMaximumMapExtentMeters(float packageRadiusMeters)
        => packageRadiusMeters > 0f
            ? packageRadiusMeters
            : DefaultMapExtentMeters;

    /// <summary>Clamps a map radius to the range supported by the current city package.</summary>
    public static float ClampMapExtentMeters(float requestedMeters, float packageRadiusMeters)
        => Mathf.Clamp(requestedMeters, GetMinimumMapExtentMeters(packageRadiusMeters), GetMaximumMapExtentMeters(packageRadiusMeters));

    private float GetPackageRadiusMeters() => metadata == null ? 0f : metadata.RadiusMeters;

    /// <summary>Uses the lowest scene geometry and the main camera's configured near clip as the closest valid overview height.</summary>
    public static float GetMinimumSourceCameraHeightMeters(float sceneMinimumY, float sourceCameraNearClipPlane)
        => sceneMinimumY + Mathf.Max(0.1f, sourceCameraNearClipPlane);

    /// <summary>Uses one city-package radius above the lowest scene geometry as the farthest valid overview height.</summary>
    public static float GetMaximumSourceCameraHeightMeters(float sceneMinimumY, float packageRadiusMeters)
        => sceneMinimumY + GetMaximumMapExtentMeters(packageRadiusMeters);

    /// <summary>Maps a valid source-camera height linearly to the supported overview-map radius.</summary>
    public static float GetMapExtentMetersForSourceCameraHeight(float sourceCameraHeight, float minimumSourceCameraHeight,
        float maximumSourceCameraHeight, float packageRadiusMeters)
    {
        var normalizedHeight = Mathf.InverseLerp(minimumSourceCameraHeight, maximumSourceCameraHeight, sourceCameraHeight);
        return Mathf.Lerp(GetMinimumMapExtentMeters(packageRadiusMeters), GetMaximumMapExtentMeters(packageRadiusMeters), normalizedHeight);
    }

    private float ResolveMapExtentMeters() => ClampMapExtentMeters(mapExtentMeters, GetPackageRadiusMeters());

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
        mesh.SetNextIndex(0);
        mesh.SetNextIndex(1);
        mesh.SetNextIndex(2);
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
