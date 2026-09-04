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
    private const string SceneAssetDirectory = "Assets/Scenes/EnvironmentCostInspection";
    private static EnvironmentCostInspectionSceneBuilder batchRunner;
    private static Task<bool> batchTask;
    private string configPath = "data/analysis-configs/ichigaya-venue.json";
    private string runtimeCityPackageConfigPath;
    private string status = "検証済みの解析設定を選択して、ローカル検証用 Scene を作成します。";
    private bool isRunning;
    private bool cancelRequested;

    [MenuItem("PLATEAU/環境コスト/検証用 Scene を作成")]
    public static void Open() => GetWindow<EnvironmentCostInspectionSceneBuilder>("環境コスト検証");

    /// <summary>Creates one city inspection Scene from -analysisConfig in Unity batch mode.</summary>
    public static void Run()
    {
        if (!Application.isBatchMode)
        {
            Debug.LogError("ENVIRONMENT_COST_INSPECTION_SCENE_FAILED Run requires Unity -batchmode.");
            EditorApplication.Exit(1);
            return;
        }

        try
        {
            var config = AnalysisRunConfig.LoadForCurrentProcess();
            batchRunner = CreateInstance<EnvironmentCostInspectionSceneBuilder>();
            var runtimePackageConfigPath = FindCommandLineValue("-runtimeCityPackageConfig");
            var packageConfig = string.IsNullOrWhiteSpace(runtimePackageConfigPath) ? null : RuntimeCityPackageConfig.Load(runtimePackageConfigPath);
            batchTask = batchRunner.CreateInspectionSceneAsync(config, packageConfig, isBatchMode: true);
            EditorApplication.update += ExitBatchWhenComplete;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            Debug.LogError("ENVIRONMENT_COST_INSPECTION_SCENE_FAILED");
            EditorApplication.Exit(1);
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("環境コスト検証用 Scene", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "解析設定とカバレッジレポートからローカル専用の Scene を作成します。CityGML の建築物・道路・地形（DEM）メッシュを LOD1 まで取り込み、" +
            "Building（レイヤー 8）、Road（レイヤー 9）、Terrain（レイヤー 10）へ collider を割り当てます。既存の Scene は変更しません。", MessageType.Info);

        using (new EditorGUI.DisabledScope(isRunning))
        {
            runtimeCityPackageConfigPath = EditorGUILayout.TextField("Runtime 都市パッケージ設定（任意）", runtimeCityPackageConfigPath);
            configPath = EditorGUILayout.TextField("解析設定", configPath);
            if (GUILayout.Button("解析設定を選択"))
            {
                var selected = EditorUtility.OpenFilePanel("解析設定", DefaultConfigDirectory(), "json");
                if (!string.IsNullOrWhiteSpace(selected)) configPath = selected;
            }
            if (GUILayout.Button("検証用 Scene を作成")) _ = CreateInspectionSceneAsync();
        }
        if (isRunning && GUILayout.Button("中止を要求"))
        {
            cancelRequested = true;
            status = "中止を要求しました。現在の CityGML 取込が終わってから後片付けを行います。";
        }
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("状態", status, EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("出力先", $"{SceneAssetDirectory}/<areaId>.unity");
    }

    private static void ExitBatchWhenComplete()
    {
        if (batchTask == null || !batchTask.IsCompleted) return;

        EditorApplication.update -= ExitBatchWhenComplete;
        var succeeded = batchTask.Status == TaskStatus.RanToCompletion && batchTask.Result;
        if (!succeeded) Debug.LogError("ENVIRONMENT_COST_INSPECTION_SCENE_FAILED");
        batchTask = null;
        batchRunner = null;
        EditorApplication.Exit(succeeded ? 0 : 1);
    }

    private async Task<bool> CreateInspectionSceneAsync(AnalysisRunConfig suppliedConfig = null, RuntimeCityPackageConfig suppliedPackageConfig = null, bool isBatchMode = false)
    {
        if (isRunning) return false;
        isRunning = true;
        cancelRequested = false;
        Scene inspectionScene = default;
        var sceneCreated = false;
        try
        {
            var config = suppliedConfig ?? AnalysisRunConfig.LoadForEditor(configPath);
            var packageConfig = suppliedPackageConfig ?? (string.IsNullOrWhiteSpace(runtimeCityPackageConfigPath) ? null : RuntimeCityPackageConfig.Load(runtimeCityPackageConfigPath));
            if (packageConfig != null && !string.Equals(packageConfig.areaId, config.areaId, StringComparison.Ordinal))
                throw new InvalidOperationException("Runtime city package config areaId must match the inspection Scene analysis config.");
            var sceneAssetPath = GetSceneAssetPath(config.areaId);
            var outputPath = Path.Combine(Application.dataPath, "Scenes", "EnvironmentCostInspection",
                Path.GetFileName(sceneAssetPath));
            ValidateLayerNames();
            var coveragePath = config.ResolvePath(config.coverageOutputPath);
            var coverage = JsonConvert.DeserializeObject<CoverageReport>(File.ReadAllText(coveragePath))
                ?? throw new InvalidOperationException("Coverage report could not be parsed.");
            if (coverage.datasets == null || coverage.datasets.Count == 0)
                throw new InvalidOperationException("Coverage report does not contain any datasets.");

            if (File.Exists(outputPath) && isBatchMode)
                throw new InvalidOperationException($"Inspection Scene already exists and batch mode will not overwrite it: {sceneAssetPath}");
            if (File.Exists(outputPath) && !EditorUtility.DisplayDialog("検証用 Scene を置き換えますか？",
                    $"「{config.areaId}」の既存ローカル検証用 Scene を置き換えます:\n{sceneAssetPath}\n\n" +
                    "この Scene は生成物で Git の管理対象外です。他地域の Scene は変更しません。", "置き換える", "キャンセル"))
            {
                status = "Scene を変更する前に作成を中止しました。";
                return false;
            }

            if (!isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                status = "現在の Scene が保存されなかったため、作成を中止しました。";
                return false;
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
                status = $"{dataset.title ?? dataset.id} を取り込み中（{++imported}/{coverage.datasets.Count}）…";
                Repaint();
                var sourceRoot = EnvironmentCostAnalyzer.FindLocalDatasetRoot(config, dataset.id);
                await EnvironmentCostAnalyzer.ImportDataset(config, dataset.id, dataset.title, sourceRoot, gridCodes, referencePoint,
                    includeRelief: true, includeVegetation: config.includeCityGmlVegetation);
            }
            ThrowIfCancellationRequested();

            var layers = EnvironmentCostAnalyzer.AssignColliderLayers(inspectionScene);
            var shadows = EnvironmentCostAnalyzer.ConfigureInspectionShadows(inspectionScene);
            Physics.SyncTransforms();
            if (layers.building == 0 || layers.road == 0 || layers.terrain == 0)
                throw new InvalidOperationException($"The inspection Scene is incomplete: Building colliders={layers.building}, Road colliders={layers.road}, Terrain colliders={layers.terrain}.");

            ConfigureRuntimePresentation(root, config, packageConfig, inspectionScene);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("Scene directory is missing."));
            EditorSceneManager.SaveScene(inspectionScene, sceneAssetPath, false);
            AssetDatabase.Refresh();
            status = $"作成しました: {sceneAssetPath}（Building={layers.building:N0}、Road={layers.road:N0}、Terrain={layers.terrain:N0}、影を落とすオブジェクト={shadows.casters:N0}）";
            Debug.Log($"ENVIRONMENT_COST_INSPECTION_SCENE_READY area={config.areaId} buildingColliders={layers.building} roadColliders={layers.road} terrainColliders={layers.terrain} shadowCasters={shadows.casters} shadowReceivers={shadows.receivers} scene={sceneAssetPath}");
            Selection.activeGameObject = root;
            return true;
        }
        catch (OperationCanceledException)
        {
            status = "中止しました。途中まで作成した検証用 Scene は保存せず閉じました。";
            Debug.LogWarning("ENVIRONMENT_COST_INSPECTION_SCENE_CANCELLED");
            CleanupPartialScene(inspectionScene, sceneCreated);
            return false;
        }
        catch (Exception exception)
        {
            status = $"失敗しました: {exception.Message}";
            Debug.LogException(exception);
            CleanupPartialScene(inspectionScene, sceneCreated);
            return false;
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

    private static string GetSceneAssetPath(string areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId) || areaId[0] == '-' || areaId[areaId.Length - 1] == '-')
            throw new InvalidOperationException("areaId must contain lowercase ASCII letters, digits, and single hyphens to create an inspection Scene.");

        var previousWasHyphen = false;
        foreach (var character in areaId)
        {
            var isLowercaseLetter = character >= 'a' && character <= 'z';
            var isDigit = character >= '0' && character <= '9';
            if (!isLowercaseLetter && !isDigit && character != '-')
                throw new InvalidOperationException("areaId must contain lowercase ASCII letters, digits, and single hyphens to create an inspection Scene.");
            if (character == '-' && previousWasHyphen)
                throw new InvalidOperationException("areaId must not contain consecutive hyphens to create an inspection Scene.");
            previousWasHyphen = character == '-';
        }

        return $"{SceneAssetDirectory}/{areaId}.unity";
    }

    private static string DefaultConfigDirectory()
    {
        var root = Directory.GetParent(Application.dataPath)?.Parent?.FullName;
        return string.IsNullOrWhiteSpace(root) ? Application.dataPath : Path.Combine(root, "data", "analysis-configs");
    }

    private static void ConfigureRuntimePresentation(GameObject root, AnalysisRunConfig config, RuntimeCityPackageConfig packageConfig, Scene scene)
    {
        EnvironmentCostRuntimeUiAssets.Ensure();
        var bounds = CalculateRenderableBounds(scene);
        var cameraObject = new GameObject("Environment Cost Runtime Camera");
        cameraObject.tag = "MainCamera";
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

        var metadata = root.AddComponent<EnvironmentCostInspectionMetadata>();
        metadata.Configure(config.areaId, config.coordinateZoneId, config.CenterLongitude, config.CenterLatitude,
            config.radiusMeters, config.date, config.timezone);
        var packageLoader = root.AddComponent<EnvironmentCostRuntimeCityPackageLoader>();
        packageLoader.Configure(packageConfig?.packageRelativePath ?? "EnvironmentCostCities", appendAreaId: packageConfig == null);
        root.AddComponent<EnvironmentCostRuntimeShadeAnalysisController>();
        root.AddComponent<EnvironmentCostRuntimePolicyScenarioController>();
        root.AddComponent<EnvironmentCostRuntimeRouteComparisonController>();
        root.AddComponent<EnvironmentCostRuntimeUiController>();
        var solarController = root.AddComponent<EnvironmentCostSolarController>();
        solarController.Configure(metadata, sun, config.hours);
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

    private static string FindCommandLineValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    [Serializable] private sealed class CoverageReport { public List<DatasetCoverage> datasets; }
    [Serializable] private sealed class DatasetCoverage { public string id; public string title; public List<string> gridCodes; }
}
