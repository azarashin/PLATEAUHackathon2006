using System;
using System.Collections;
using System.IO;
using UnityEngine;

/// <summary>Verifies the city package bundled under StreamingAssets before Runtime editing or analysis starts.</summary>
public sealed class EnvironmentCostRuntimeCityPackageLoader : MonoBehaviour
{
    public enum PackageState { NotStarted, Loading, Ready, Missing, Invalid }

    [SerializeField] private string packageRoot = "EnvironmentCostCities";
    [SerializeField] private bool appendAreaIdToPackageRoot = true;
    [SerializeField] private bool verifyOnStart = true;
    [SerializeField] private bool showStatusOverlay = true;
    [SerializeField] private PackageState state;
    [SerializeField, TextArea] private string statusMessage;
    private bool loadRequested;

    public PackageState State => state;
    public string StatusMessage => statusMessage;
    public EnvironmentCostRuntimeCityPackageManifest Manifest { get; private set; }
    public string PackageRootPath { get; private set; }

    public void Configure(string newPackageRoot)
    {
        packageRoot = newPackageRoot;
        appendAreaIdToPackageRoot = true;
    }

    /// <summary>Configures the exact StreamingAssets-relative directory of a generated city package.</summary>
    public void Configure(string newPackageRoot, bool appendAreaId)
    {
        packageRoot = newPackageRoot;
        appendAreaIdToPackageRoot = appendAreaId;
    }

    // Supports inspection Scenes generated before this component was introduced. New Scenes receive the
    // component from EnvironmentCostInspectionSceneBuilder; old local, ignored Scenes get it at Player startup.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToLegacyInspectionScene()
    {
        var metadata = FindFirstObjectByType<EnvironmentCostInspectionMetadata>();
        if (metadata == null || metadata.GetComponent<EnvironmentCostRuntimeCityPackageLoader>() != null) return;
        metadata.gameObject.AddComponent<EnvironmentCostRuntimeCityPackageLoader>();
    }

    private void Start()
    {
        if (verifyOnStart) EnsureLoadStarted();
    }

    /// <summary>Starts verification once. Consumers can call this safely before waiting for package state.</summary>
    public void EnsureLoadStarted()
    {
        if (state != PackageState.NotStarted || loadRequested) return;
        loadRequested = true;
        StartCoroutine(LoadAndVerify());
    }

    /// <summary>Resolves either the legacy area-root convention or an explicitly configured package directory.</summary>
    public static string ResolvePackageRootPath(string streamingAssetsPath, string configuredPackageRoot, string areaId, bool appendAreaId)
    {
        var root = Path.Combine(streamingAssetsPath, configuredPackageRoot);
        return appendAreaId ? Path.Combine(root, areaId) : root;
    }

    public IEnumerator LoadAndVerify()
    {
        loadRequested = true;
        state = PackageState.Loading;
        statusMessage = "都市データパッケージを検証中…";
        Manifest = null;
        PackageRootPath = null;
        yield return null;

        try
        {
            var metadata = GetComponent<EnvironmentCostInspectionMetadata>();
            if (metadata == null) throw new InvalidOperationException("Inspection metadata is missing from this Runtime scene.");
            var root = ResolvePackageRootPath(Application.streamingAssetsPath, packageRoot, metadata.AreaId, appendAreaIdToPackageRoot);
            var manifestPath = Path.Combine(root, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                state = PackageState.Missing;
                statusMessage = $"都市データパッケージがありません: {manifestPath}";
                yield break;
            }

            var manifest = JsonUtility.FromJson<EnvironmentCostRuntimeCityPackageManifest>(File.ReadAllText(manifestPath));
            if (manifest == null) throw new InvalidOperationException("Package manifest could not be parsed.");
            manifest.ValidateStructure();
            ValidateAgainstScene(manifest, metadata);
            foreach (var file in manifest.files)
            {
                var fullPath = Path.Combine(root, file.relativePath);
                if (!File.Exists(fullPath)) throw new FileNotFoundException($"Package file is missing: {file.relativePath}", fullPath);
                if (new FileInfo(fullPath).Length != file.bytes) throw new InvalidOperationException($"Package file size differs: {file.relativePath}");
                if (!string.Equals(EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(fullPath), file.sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Package file digest differs: {file.relativePath}");
            }
            ValidatePlaceLabelFiles(manifest, root);

            Manifest = manifest;
            PackageRootPath = root;
            state = PackageState.Ready;
            statusMessage = $"都市データ準備完了: {manifest.displayName} / {manifest.version} ({manifest.files.Length} files)";
            Debug.Log($"ENVIRONMENT_COST_RUNTIME_CITY_PACKAGE_READY area={manifest.areaId} version={manifest.version} files={manifest.files.Length}");
        }
        catch (Exception exception)
        {
            state = PackageState.Invalid;
            statusMessage = $"都市データパッケージを使用できません: {exception.Message}";
            Debug.LogException(exception);
            Debug.LogError("ENVIRONMENT_COST_RUNTIME_CITY_PACKAGE_FAILED");
        }
    }

    private static void ValidatePlaceLabelFiles(EnvironmentCostRuntimeCityPackageManifest manifest, string root)
    {
        if (!string.Equals(manifest.schemaVersion, "environment-cost-runtime-city-package-0.2", StringComparison.Ordinal)) return;
        var labels = JsonUtility.FromJson<EnvironmentCostPlaceLabels>(File.ReadAllText(Path.Combine(root, "place-labels.json")));
        var report = JsonUtility.FromJson<EnvironmentCostPlaceLabelReport>(File.ReadAllText(Path.Combine(root, "place-label-report.json")));
        if (labels == null || report == null || labels.schemaVersion != "environment-cost-place-labels-0.1" || report.schemaVersion != "environment-cost-place-label-report-0.1" ||
            labels.areaId != manifest.areaId || report.areaId != manifest.areaId || labels.coordinateZoneId != manifest.coordinateZoneId || report.coordinateZoneId != manifest.coordinateZoneId)
            throw new InvalidOperationException("Runtime city package place-label metadata does not match its manifest.");
    }

    private static void ValidateAgainstScene(EnvironmentCostRuntimeCityPackageManifest manifest, EnvironmentCostInspectionMetadata metadata)
    {
        if (!string.Equals(manifest.areaId, metadata.AreaId, StringComparison.Ordinal) || manifest.coordinateZoneId != metadata.CoordinateZoneId ||
            Math.Abs(manifest.center[0] - metadata.Longitude) > 0.0001 || Math.Abs(manifest.center[1] - metadata.Latitude) > 0.0001 ||
            Math.Abs(manifest.radiusMeters - metadata.RadiusMeters) > 0.1)
            throw new InvalidOperationException("Package area, CRS, or extent does not match the loaded Runtime scene.");
        foreach (var layer in manifest.scene.requiredLayers)
        {
            if (LayerMask.NameToLayer(layer.name) != layer.layer)
                throw new InvalidOperationException($"Required layer is not configured: {layer.name}={layer.layer}.");
            var hasCollider = false;
            foreach (var collider in FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (collider.gameObject.layer == layer.layer) { hasCollider = true; break; }
            }
            if (!hasCollider) throw new InvalidOperationException($"Runtime scene has no {layer.role} collider on layer {layer.name}.");
        }
    }

    // Package errors are surfaced through the Runtime UI controller after UI Toolkit starts.
    private void LegacyOnGUI()
    {
        if (!showStatusOverlay || state == PackageState.Ready || state == PackageState.NotStarted) return;
        GUI.Box(new Rect(12, 12, 680, 52), statusMessage);
    }
}
