using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PLATEAU.Geometries;
using PLATEAU.Native;
using UnityEditor;
using UnityEngine;

public sealed class HourlyEnvironmentCostHeatmapWindow : EditorWindow
{
    private HeatmapDocument document;
    private string loadedPath;
    private int selectedHourIndex;
    private int displayLimit = 200000;
    private float lineWidth = 3.0f;

    [MenuItem("PLATEAU/Environment Cost/Hourly Heatmap")]
    public static void Open() => GetWindow<HourlyEnvironmentCostHeatmapWindow>("Hourly Cost Heatmap");

    private void OnEnable() => SceneView.duringSceneGui += DrawScene;
    private void OnDisable() => SceneView.duringSceneGui -= DrawScene;

    private void OnGUI()
    {
        EditorGUILayout.LabelField("時刻別道路日陰率", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("橙は日向が多い道路、緑は日陰が多い道路、灰色は欠測です。表示値は解析値であり、探索用の重みではありません。", MessageType.Info);
        if (GUILayout.Button("完了済み環境コストJSONを開く"))
        {
            var path = EditorUtility.OpenFilePanel("環境コストJSON", DefaultOutputDirectory(), "json");
            if (!string.IsNullOrWhiteSpace(path)) Load(path);
        }
        if (document == null)
        {
            EditorGUILayout.LabelField("データが読み込まれていません。");
            return;
        }

        EditorGUILayout.LabelField("ファイル", loadedPath);
        EditorGUILayout.LabelField("地域", document.areaId);
        var hours = AvailableHours();
        var labels = hours.Select(hour => $"{hour:00}:00").ToArray();
        var nextIndex = EditorGUILayout.Popup("表示時刻", selectedHourIndex, labels);
        displayLimit = EditorGUILayout.IntSlider("最大表示辺数", displayLimit, 1000, Math.Max(1000, document.edges.Count));
        lineWidth = EditorGUILayout.Slider("線幅", lineWidth, 1.0f, 8.0f);
        if (nextIndex != selectedHourIndex)
        {
            selectedHourIndex = nextIndex;
            SceneView.RepaintAll();
        }
        var selectedHour = hours[selectedHourIndex];
        var slices = document.edges.Select(edge => edge.hourly.First(value => value.hour == selectedHour)).ToArray();
        EditorGUILayout.LabelField("表示辺", $"{Math.Min(displayLimit, document.edges.Count):N0}/{document.edges.Count:N0}");
        EditorGUILayout.LabelField("available / partial / missing",
            $"{slices.Count(value => value.status == "available"):N0} / {slices.Count(value => value.status == "partial"):N0} / {slices.Count(value => value.status == "missing"):N0}");
    }

    private void Load(string path)
    {
        var parsed = JsonConvert.DeserializeObject<HeatmapDocument>(File.ReadAllText(path));
        if (parsed == null || parsed.schemaVersion != "environment-cost-analysis-0.2" || parsed.status != "completed")
            throw new InvalidOperationException("完了済みの environment-cost-analysis-0.2 ではありません。");
        if (parsed.center == null || parsed.center.Length != 2 || parsed.edges == null || parsed.edges.Count == 0)
            throw new InvalidOperationException("地図表示に必要な地域・道路情報がありません。");
        var hours = parsed.edges[0].hourly?.Select(value => value.hour).ToArray() ?? Array.Empty<int>();
        if (hours.Length == 0 || parsed.edges.Any(edge => edge.hourly == null || !edge.hourly.Select(value => value.hour).SequenceEqual(hours)))
            throw new InvalidOperationException("道路ごとの時刻スライスが一致しません。");
        document = parsed;
        loadedPath = path;
        selectedHourIndex = 0;
        displayLimit = Math.Min(Math.Max(1000, document.edges.Count), 200000);
        SceneView.RepaintAll();
        Repaint();
    }

    private void DrawScene(SceneView sceneView)
    {
        if (document == null) return;
        var hours = AvailableHours();
        if (selectedHourIndex < 0 || selectedHourIndex >= hours.Length) return;
        var selectedHour = hours[selectedHourIndex];
        using var worldReference = GeoReference.Create(new PlateauVector3d(0.0, 0.0, 0.0), 1.0f,
            CoordinateSystem.EUN, document.coordinateZoneId);
        var referencePoint = worldReference.Project(new GeoCoordinate(document.center[1], document.center[0], 0.0));
        using var localReference = GeoReference.Create(referencePoint, 1.0f, CoordinateSystem.EUN, document.coordinateZoneId);

        foreach (var edge in document.edges.Take(displayLimit))
        {
            var hourly = edge.hourly.First(value => value.hour == selectedHour);
            Handles.color = HeatmapColor(hourly);
            var points = edge.coordinates.Select(coordinate =>
            {
                var projected = localReference.Project(new GeoCoordinate(coordinate[1], coordinate[0], 0.0));
                return new Vector3((float)projected.X, 2.0f, (float)projected.Z);
            }).ToArray();
            if (points.Length >= 2) Handles.DrawAAPolyLine(lineWidth, points);
        }
    }

    private int[] AvailableHours() => document.edges[0].hourly.Select(value => value.hour).ToArray();

    private static Color HeatmapColor(HeatmapHourly value)
    {
        if (value.status == "missing" || !value.shadeRatio.HasValue) return new Color(0.39f, 0.45f, 0.55f, 0.9f);
        var orange = new Color(0.96f, 0.62f, 0.04f, 0.95f);
        var green = new Color(0.02f, 0.47f, 0.34f, 0.95f);
        var color = Color.Lerp(orange, green, Mathf.Clamp01((float)value.shadeRatio.Value));
        if (value.status == "partial") color.a = 0.65f;
        return color;
    }

    private static string DefaultOutputDirectory()
    {
        var current = Directory.GetParent(Application.dataPath)?.Parent;
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return Path.Combine(current.FullName, "data", "generated");
            current = current.Parent;
        }
        return Application.dataPath;
    }

    [Serializable] private sealed class HeatmapDocument { public string schemaVersion; public string status; public string areaId; public double[] center; public int coordinateZoneId; public List<HeatmapEdge> edges; }
    [Serializable] private sealed class HeatmapEdge { public double[][] coordinates; public HeatmapHourly[] hourly; }
    [Serializable] private sealed class HeatmapHourly { public int hour; public string status; public double? shadeRatio; }
}
