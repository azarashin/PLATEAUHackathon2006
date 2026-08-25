using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PLATEAU.Geometries;
using PLATEAU.Native;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Creates a disposable, local Scene containing Building, Road, and Relief CityGML
/// needed to inspect an existing environment-cost result.
/// </summary>
public sealed class EnvironmentCostInspectionSceneBuilder : EditorWindow
{
    private const int BuildingLayer = 8;
    private const int RoadLayer = 9;
    private const int TerrainLayer = 10;
    private const string SceneAssetPath = "Assets/Scenes/EnvironmentCostInspection.unity";
    private string configPath = "data/analysis-configs/ichigaya-venue.json";
    private string status = "Choose a validated analysis config to create a local inspection Scene.";
    private bool isRunning;
    private bool cancelRequested;

    [MenuItem("PLATEAU/Environment Cost/Create Inspection Scene")]
    public static void Open() => GetWindow<EnvironmentCostInspectionSceneBuilder>("Environment Cost Inspection");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Environment Cost Inspection Scene", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Creates a local, ignored Scene from the config and coverage report. CityGML Building, Road, and Relief (DEM) meshes up to LOD1 are imported; " +
            "colliders are assigned to Building (layer 8), Road (layer 9), and Terrain (layer 10). Existing Scenes are not modified.", MessageType.Info);

        using (new EditorGUI.DisabledScope(isRunning))
        {
            configPath = EditorGUILayout.TextField("Analysis config", configPath);
            if (GUILayout.Button("Choose analysis config"))
            {
                var selected = EditorUtility.OpenFilePanel("Analysis config", DefaultConfigDirectory(), "json");
                if (!string.IsNullOrWhiteSpace(selected)) configPath = selected;
            }
            if (GUILayout.Button("Create inspection Scene")) _ = CreateInspectionSceneAsync();
        }
        if (isRunning && GUILayout.Button("Request cancellation"))
        {
            cancelRequested = true;
            status = "Cancellation requested. The current CityGML import will finish before cleanup.";
        }
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Status", status, EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("Output", SceneAssetPath);
    }

    private async Task CreateInspectionSceneAsync()
    {
        if (isRunning) return;
        isRunning = true;
        cancelRequested = false;
        Scene inspectionScene = default;
        var sceneCreated = false;
        try
        {
            var config = AnalysisRunConfig.LoadForEditor(configPath);
            ValidateLayerNames();
            var coveragePath = config.ResolvePath(config.coverageOutputPath);
            var coverage = JsonConvert.DeserializeObject<CoverageReport>(File.ReadAllText(coveragePath))
                ?? throw new InvalidOperationException("Coverage report could not be parsed.");
            if (coverage.datasets == null || coverage.datasets.Count == 0)
                throw new InvalidOperationException("Coverage report does not contain any datasets.");

            var outputPath = Path.Combine(Application.dataPath, "Scenes", "EnvironmentCostInspection.unity");
            if (File.Exists(outputPath) && !EditorUtility.DisplayDialog("Replace inspection Scene?",
                    "The previous local inspection Scene will be replaced. It is generated and ignored by Git.", "Replace", "Cancel"))
            {
                status = "Creation cancelled before any Scene was changed.";
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                status = "Creation cancelled because the current Scene was not saved.";
                return;
            }

            // Unity cannot create an additive Scene while its only open Scene is an unsaved Untitled Scene.
            // The inspection Scene is intentionally isolated, so switch to it after offering Unity's normal
            // save confirmation for any user changes in the current Scene.
            inspectionScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            sceneCreated = true;
            var root = new GameObject($"Environment Cost Inspection - {config.areaId}");
            EditorSceneManager.MarkSceneDirty(inspectionScene);

            using var worldReference = GeoReference.Create(new PlateauVector3d(0.0, 0.0, 0.0), 1.0f,
                CoordinateSystem.EUN, config.coordinateZoneId);
            var referencePoint = worldReference.Project(new GeoCoordinate(config.CenterLatitude, config.CenterLongitude, 0.0));
            var imported = 0;
            foreach (var dataset in coverage.datasets)
            {
                ThrowIfCancellationRequested();
                var gridCodes = MeshCoverageAnalyzer.NormalizeGridCodes(dataset.gridCodes).ToArray();
                if (gridCodes.Length == 0) continue;
                status = $"Importing {dataset.title ?? dataset.id} ({++imported}/{coverage.datasets.Count})…";
                Repaint();
                var sourceRoot = EnvironmentCostAnalyzer.FindLocalDatasetRoot(config, dataset.id);
                await EnvironmentCostAnalyzer.ImportDataset(config, dataset.id, dataset.title, sourceRoot, gridCodes, referencePoint, includeRelief: true);
            }
            ThrowIfCancellationRequested();

            var layers = EnvironmentCostAnalyzer.AssignColliderLayers(inspectionScene);
            var shadows = EnvironmentCostAnalyzer.ConfigureInspectionShadows(inspectionScene);
            Physics.SyncTransforms();
            if (layers.building == 0 || layers.road == 0 || layers.terrain == 0)
                throw new InvalidOperationException($"The inspection Scene is incomplete: Building colliders={layers.building}, Road colliders={layers.road}, Terrain colliders={layers.terrain}.");

            ConfigureRuntimePresentation(root, config, inspectionScene);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("Scene directory is missing."));
            EditorSceneManager.SaveScene(inspectionScene, SceneAssetPath, false);
            AssetDatabase.Refresh();
            status = $"Created {SceneAssetPath}: Building={layers.building:N0}, Road={layers.road:N0}, Terrain={layers.terrain:N0}, shadow casters={shadows.casters:N0}.";
            Debug.Log($"ENVIRONMENT_COST_INSPECTION_SCENE_READY area={config.areaId} buildingColliders={layers.building} roadColliders={layers.road} terrainColliders={layers.terrain} shadowCasters={shadows.casters} shadowReceivers={shadows.receivers} scene={SceneAssetPath}");
            Selection.activeGameObject = root;
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled. The partial inspection Scene was closed without saving.";
            Debug.LogWarning("ENVIRONMENT_COST_INSPECTION_SCENE_CANCELLED");
            CleanupPartialScene(inspectionScene, sceneCreated);
        }
        catch (Exception exception)
        {
            status = $"Failed: {exception.Message}";
            Debug.LogException(exception);
            CleanupPartialScene(inspectionScene, sceneCreated);
        }
        finally
        {
            isRunning = false;
            cancelRequested = false;
            Repaint();
        }
    }

    private void ThrowIfCancellationRequested()
    {
        if (cancelRequested) throw new OperationCanceledException();
    }

    private static void CleanupPartialScene(Scene scene, bool wasCreated)
    {
        if (wasCreated && scene.IsValid() && scene.isLoaded)
            EditorSceneManager.CloseScene(scene, true);
    }

    private static void ValidateLayerNames()
    {
        if (LayerMask.LayerToName(BuildingLayer) != "Building" || LayerMask.LayerToName(RoadLayer) != "Road" ||
            LayerMask.LayerToName(TerrainLayer) != "Terrain")
            throw new InvalidOperationException("ProjectSettings/TagManager.asset must reserve layers 8=Building, 9=Road, and 10=Terrain. Do not replace occupied layers.");
    }

    private static string DefaultConfigDirectory()
    {
        var root = Directory.GetParent(Application.dataPath)?.Parent?.FullName;
        return string.IsNullOrWhiteSpace(root) ? Application.dataPath : Path.Combine(root, "data", "analysis-configs");
    }

    private static void ConfigureRuntimePresentation(GameObject root, AnalysisRunConfig config, Scene scene)
    {
        var bounds = CalculateRenderableBounds(scene);
        var cameraObject = new GameObject("Environment Cost Runtime Camera");
        SceneManager.MoveGameObjectToScene(cameraObject, scene);
        var camera = cameraObject.AddComponent<Camera>();
        cameraObject.AddComponent<AudioListener>();
        cameraObject.AddComponent<EnvironmentCostInspectionFlyCamera>();
        camera.transform.position = bounds.center + new Vector3(bounds.extents.x, Mathf.Max(bounds.extents.y, 100f), -bounds.extents.z - 50f);
        camera.transform.LookAt(bounds.center);
        camera.farClipPlane = Mathf.Max(1000f, bounds.size.magnitude * 4f);
        camera.clearFlags = CameraClearFlags.Skybox;

        var sunObject = new GameObject("Environment Cost Inspection Sun");
        SceneManager.MoveGameObjectToScene(sunObject, scene);
        var sun = sunObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.shadows = LightShadows.Soft;
        sun.intensity = 1.0f;
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var metadata = root.AddComponent<EnvironmentCostInspectionMetadata>();
        metadata.Configure(config.areaId, config.coordinateZoneId, config.CenterLongitude, config.CenterLatitude, config.radiusMeters);
    }

    private static Bounds CalculateRenderableBounds(Scene scene)
    {
        var hasBounds = false;
        var result = new Bounds(Vector3.zero, Vector3.one);
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (renderer.gameObject.scene != scene) continue;
            if (!hasBounds) { result = renderer.bounds; hasBounds = true; }
            else result.Encapsulate(renderer.bounds);
        }
        return hasBounds ? result : new Bounds(Vector3.zero, new Vector3(100f, 100f, 100f));
    }

    [Serializable] private sealed class CoverageReport { public List<DatasetCoverage> datasets; }
    [Serializable] private sealed class DatasetCoverage { public string id; public string title; public List<string> gridCodes; }
}
