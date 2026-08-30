using System;
using System.Collections;
using System.IO;
using UnityEngine;

/// <summary>Verifies the city package bundled under StreamingAssets before Runtime editing or analysis starts.</summary>
public sealed class EnvironmentCostRuntimeCityPackageLoader : MonoBehaviour
{
    public enum PackageState { NotStarted, Loading, Ready, Missing, Invalid }

    [SerializeField] private string packageRoot = "EnvironmentCostCities";
    [SerializeField] private bool verifyOnStart = true;
    [SerializeField] private bool showStatusOverlay = true;
    [SerializeField] private PackageState state;
    [SerializeField, TextArea] private string statusMessage;

    public PackageState State => state;
    public string StatusMessage => statusMessage;
    public EnvironmentCostRuntimeCityPackageManifest Manifest { get; private set; }
    public string PackageRootPath { get; private set; }

    public void Configure(string newPackageRoot)
    {
        packageRoot = newPackageRoot;
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
        if (verifyOnStart) StartCoroutine(LoadAndVerify());
    }

    public IEnumerator LoadAndVerify()
    {
        state = PackageState.Loading;
        statusMessage = "都市データパッケージを検証中…";
        Manifest = null;
        PackageRootPath = null;
        yield return null;

        try
        {
            var metadata = GetComponent<EnvironmentCostInspectionMetadata>();
            if (metadata == null) throw new InvalidOperationException("Inspection metadata is missing from this Runtime scene.");
            var root = Path.Combine(Application.streamingAssetsPath, packageRoot, metadata.AreaId);
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
