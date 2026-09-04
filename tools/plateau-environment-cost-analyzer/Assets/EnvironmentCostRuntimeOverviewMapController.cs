using System.Collections;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.UIElements;

/// <summary>
/// Renders a north-up, camera-following overview map for Runtime inspection Scenes.
/// The map is intentionally display-only; scale controls and place labels are added separately.
/// </summary>
public sealed class EnvironmentCostRuntimeOverviewMapController : MonoBehaviour
{
    private const int BuildingLayer = 8;
    private const int RoadLayer = 9;
    private const int TerrainLayer = 10;
    private const float MinimumMapExtentMeters = 200f;
    private const float MaximumMapExtentMeters = 8000f;
    public const float MovingRefreshIntervalSeconds = 0.2f;
    public const float IdleRefreshIntervalSeconds = 1.0f;
    private static readonly ProfilerMarker OverviewMapRenderMarker = new ProfilerMarker("EnvironmentCost.OverviewMap.Render");

    private EnvironmentCostInspectionMetadata metadata;
    private Camera sourceCamera;
    private Camera overviewCamera;
    private RenderTexture renderTexture;
    private VisualElement mapContainer;
    private Button visibilityButton;
    private bool isVisible = true;
    private float nextRefreshTime;
    private Vector3 lastRenderedSourcePosition;
    private bool hasRendered;
    private float mapCameraY;

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

    /// <summary>Builds the display-only overview map UI. This does not add controls for map scale.</summary>
    public void BuildUi(VisualElement root)
    {
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
        root.Add(mapContainer);

        EnsureOverviewCamera();
        image.image = renderTexture;
        UpdateVisibilityUi();
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying || !isVisible || Time.unscaledTime < nextRefreshTime || !EnsureOverviewCamera()) return;

        var sourcePosition = sourceCamera.transform.position;
        var moved = !hasRendered || HorizontalDistanceSquared(sourcePosition, lastRenderedSourcePosition) > 0.25f;
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

    private float ResolveMapExtentMeters()
    {
        var configuredRadius = metadata == null ? 0f : metadata.RadiusMeters;
        return Mathf.Clamp(configuredRadius > 0f ? configuredRadius : 500f,
            MinimumMapExtentMeters, MaximumMapExtentMeters);
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
