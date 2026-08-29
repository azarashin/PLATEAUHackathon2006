using System;
using System.Collections.Generic;
using System.IO;
using PLATEAU.Geometries;
using PLATEAU.Native;
using UnityEditor;
using UnityEngine;

public sealed class EnvironmentCostPolicyScenarioWindow : EditorWindow
{
    private string configPath = "data/analysis-configs/ichigaya-venue-policy-demo.json";
    private string scenarioPath = "data/policy-scenarios/ichigaya-demo-shade.json";
    private EnvironmentCostPolicyScenario scenario = new EnvironmentCostPolicyScenario { id = "ichigaya-demo-shade" };
    private Vector2 scroll;

    [MenuItem("PLATEAU/環境コスト/施策シナリオ")]
    public static void Open() => GetWindow<EnvironmentCostPolicyScenarioWindow>("施策シナリオ");

    private void OnGUI()
    {
        scenario ??= new EnvironmentCostPolicyScenario();
        scenario.facilities ??= new System.Collections.Generic.List<EnvironmentCostPolicyFacility>();
        EditorGUILayout.HelpBox("街路樹・人工シェードの位置を編集し、シナリオJSONとして保存します。座標を変更すると設備を移動できます。現在は安全のため、シナリオ変更後は全範囲を再計算します。", MessageType.Info);
        configPath = EditorGUILayout.TextField("解析設定", configPath);
        scenarioPath = EditorGUILayout.TextField("シナリオ JSON", scenarioPath);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("読み込む")) LoadScenario();
            if (GUILayout.Button("保存")) SaveScenario();
            if (GUILayout.Button("Scene でプレビュー")) PreviewScenario();
        }

        scenario.id = EditorGUILayout.TextField("シナリオ ID", scenario.id);
        EditorGUILayout.LabelField("再計算範囲", "全範囲（シナリオ変更時は時刻別キャッシュをすべて無効化）");
        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (var index = 0; index < scenario.facilities.Count; index++)
        {
            var facility = scenario.facilities[index];
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"施策 {index + 1}", EditorStyles.boldLabel);
            facility.id = EditorGUILayout.TextField("ID", facility.id);
            var selectedType = EditorGUILayout.Popup("種別", facility.type == "shade" ? 1 : 0, new[] { "樹木", "人工日よけ" }) == 0 ? "tree" : "shade";
            if (selectedType != facility.type)
            {
                facility.type = selectedType;
                GUIUtility.ExitGUI();
            }
            facility.latitude = EditorGUILayout.DoubleField("緯度", facility.latitude);
            facility.longitude = EditorGUILayout.DoubleField("経度", facility.longitude);
            facility.heightMeters = EditorGUILayout.DoubleField("高さ（m）", facility.heightMeters);
            if (facility.type == "tree") facility.radiusMeters = EditorGUILayout.DoubleField("樹冠半径（m）", facility.radiusMeters);
            else
            {
                facility.widthMeters = EditorGUILayout.DoubleField("幅（m）", facility.widthMeters);
                facility.depthMeters = EditorGUILayout.DoubleField("奥行き（m）", facility.depthMeters);
            }
            if (GUILayout.Button("この施策を削除")) { scenario.facilities.RemoveAt(index); GUIUtility.ExitGUI(); }
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("樹木を追加"))
            {
                scenario.facilities.Add(NewFacility("tree"));
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("人工日よけを追加"))
            {
                scenario.facilities.Add(NewFacility("shade"));
                GUIUtility.ExitGUI();
            }
        }
    }

    private EnvironmentCostPolicyFacility NewFacility(string type)
    {
        var config = AnalysisRunConfig.LoadForEditor(configPath);
        return new EnvironmentCostPolicyFacility
        {
            id = $"{type}-{scenario.facilities.Count + 1}", type = type,
            latitude = config.CenterLatitude, longitude = config.CenterLongitude
        };
    }

    private void LoadScenario()
    {
        try
        {
            var config = AnalysisRunConfig.LoadForEditor(configPath);
            var path = config.ResolvePath(scenarioPath);
            scenario = File.Exists(path)
                ? Newtonsoft.Json.JsonConvert.DeserializeObject<EnvironmentCostPolicyScenario>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Scenario could not be parsed.")
                : new EnvironmentCostPolicyScenario { id = Path.GetFileNameWithoutExtension(path) };
            scenario.Validate(path);
        }
        catch (Exception exception) { Debug.LogException(exception); ShowNotification(new GUIContent("読み込みに失敗しました。Console を確認してください。")); }
    }

    private void SaveScenario()
    {
        try
        {
            var config = AnalysisRunConfig.LoadForEditor(configPath);
            scenario.Save(config.ResolvePath(scenarioPath));
            ShowNotification(new GUIContent("シナリオを保存しました。"));
        }
        catch (Exception exception) { Debug.LogException(exception); ShowNotification(new GUIContent("保存に失敗しました。Console を確認してください。")); }
    }

    private void PreviewScenario()
    {
        try
        {
            var config = AnalysisRunConfig.LoadForEditor(configPath);
            var previews = new List<GameObject>();
            foreach (var candidate in GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (candidate != null && candidate.name.StartsWith("EnvironmentCostScenario-", StringComparison.Ordinal)) previews.Add(candidate);
            }
            foreach (var preview in previews) DestroyImmediate(preview);
            using var centerReference = GeoReference.Create(new PlateauVector3d(0.0, 0.0, 0.0), 1.0f, CoordinateSystem.EUN, config.coordinateZoneId);
            var referencePoint = centerReference.Project(new GeoCoordinate(config.CenterLatitude, config.CenterLongitude, 0.0));
            using var localReference = GeoReference.Create(referencePoint, 1.0f, CoordinateSystem.EUN, config.coordinateZoneId);
            EnvironmentCostAnalyzer.CreateScenarioFacilities(scenario, localReference);
            Physics.SyncTransforms();
            ShowNotification(new GUIContent("Scene のプレビューを更新しました。"));
        }
        catch (Exception exception) { Debug.LogException(exception); ShowNotification(new GUIContent("プレビューに失敗しました。Console を確認してください。")); }
    }
}
