using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PLATEAU.Geometries;
using PLATEAU.Native;
using UnityEditor;
using UnityEngine;

public sealed class HourlyEnvironmentCostHeatmapWindow : EditorWindow
{
    private const int BuildingLayer = 8;
    private const int RoadLayer = 9;
    private const float SampleMarkerSize = 0.8f;
    private HeatmapDocument document;
    private string loadedPath;
    private int selectedHourIndex;
    private int selectedEdgeIndex;
    private int lastFocusedEdgeIndex = -1;
    private int displayLimit = 200000;
    private int sampleDrawLimit = 200;
    private float lineWidth = 3.0f;
    private bool drawSelectedEdgeSamples = true;
    private SampleInspection lastInspection;

    [MenuItem("PLATEAU/Environment Cost/Hourly Heatmap")]
    public static void Open() => GetWindow<HourlyEnvironmentCostHeatmapWindow>("Hourly Cost Heatmap");

    private void OnEnable() => SceneView.duringSceneGui += DrawScene;
    private void OnDisable() => SceneView.duringSceneGui -= DrawScene;

    private void OnGUI()
    {
        EditorGUILayout.LabelField("時刻別道路日陰率", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "道路は保存済みの時刻別解析値で色付けします。道路辺を一つ選ぶと、CityGMLを読み込んだSceneで歩行者位置のサンプルを確認できます。Raycastと描画の対象は選択辺だけです。",
            MessageType.Info);
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
        EditorGUILayout.LabelField("解析設定", $"{document.settings.date} ({document.settings.timezone}), サンプル間隔 {document.settings.sampleSpacingMeters:F1} m, 歩行者高さ {document.settings.pedestrianHeightMeters:F2} m");
        EditorGUI.BeginChangeCheck();
        var hours = AvailableHours();
        var labels = hours.Select(hour => $"{hour:00}:00").ToArray();
        var nextHourIndex = EditorGUILayout.Popup("表示時刻", selectedHourIndex, labels);
        displayLimit = EditorGUILayout.IntSlider("最大表示辺数", displayLimit, 1000, Math.Max(1000, document.edges.Count));
        lineWidth = EditorGUILayout.Slider("道路の線幅", lineWidth, 1.0f, 8.0f);
        EditorGUILayout.Space();

        var nextSelectedEdgeIndex = EditorGUILayout.IntSlider("確認する道路辺", selectedEdgeIndex, 0, document.edges.Count - 1);
        if (nextSelectedEdgeIndex != selectedEdgeIndex)
        {
            selectedEdgeIndex = nextSelectedEdgeIndex;
            FocusSelectedEdge();
        }
        var selectedEdge = document.edges[selectedEdgeIndex];
        var selectedHourly = selectedEdge.hourly.First(value => value.hour == hours[selectedHourIndex]);
        EditorGUILayout.LabelField("選択辺", string.IsNullOrWhiteSpace(selectedEdge.id) ? $"#{selectedEdgeIndex + 1}" : selectedEdge.id);
        EditorGUILayout.LabelField("保存済み解析値", $"{selectedHourly.status}, 日陰率 {FormatRatio(selectedHourly.shadeRatio)}, サンプル {selectedEdge.validSampleCount}/{selectedEdge.sampleCount}（道路面未照合: {selectedEdge.noGroundSampleCount}）");
        drawSelectedEdgeSamples = EditorGUILayout.Toggle("選択辺のサンプルを描画", drawSelectedEdgeSamples);
        sampleDrawLimit = EditorGUILayout.IntSlider("最大サンプル描画数", sampleDrawLimit, 10, 1000);
        var sceneSettingsChanged = EditorGUI.EndChangeCheck();
        EditorGUILayout.HelpBox("サンプル表示: 緑=建物による日陰、橙=日向、赤=道路面未照合。紫の矢印は選択時刻に解析で使用した太陽方向です。", MessageType.None);
        if (lastInspection != null)
        {
            EditorGUILayout.LabelField("Scene確認", $"描画 {lastInspection.drawnSamples}/{lastInspection.totalSamples}, 日陰 {lastInspection.shadedSamples}, 日向 {lastInspection.sunlitSamples}, 道路面未照合 {lastInspection.noGroundSamples}");
            EditorGUILayout.LabelField("太陽", $"方位 {lastInspection.azimuthDegrees:F1}°, 高度 {lastInspection.elevationDegrees:F1}°");
        }

        if (nextHourIndex != selectedHourIndex || sceneSettingsChanged)
        {
            selectedHourIndex = nextHourIndex;
            lastInspection = null;
            SceneView.RepaintAll();
        }
        var slices = document.edges.Select(edge => edge.hourly.First(value => value.hour == hours[selectedHourIndex])).ToArray();
        EditorGUILayout.LabelField("表示辺", $"{Math.Min(displayLimit, document.edges.Count):N0}/{document.edges.Count:N0}");
        EditorGUILayout.LabelField("available / partial / missing", $"{slices.Count(value => value.status == "available"):N0} / {slices.Count(value => value.status == "partial"):N0} / {slices.Count(value => value.status == "missing"):N0}");
    }

    private void Load(string path)
    {
        var parsed = JsonConvert.DeserializeObject<HeatmapDocument>(File.ReadAllText(path));
        if (parsed == null || parsed.schemaVersion != "environment-cost-analysis-0.2" || parsed.status != "completed")
            throw new InvalidOperationException("Only completed environment-cost-analysis-0.2 output can be displayed.");
        if (parsed.center == null || parsed.center.Length != 2 || parsed.radiusMeters <= 0.0 || parsed.edges == null || parsed.edges.Count == 0 || parsed.settings == null || string.IsNullOrWhiteSpace(parsed.settings.date) || string.IsNullOrWhiteSpace(parsed.settings.timezone) || parsed.settings.sampleSpacingMeters <= 0.0 || parsed.settings.pedestrianHeightMeters < 0.0)
            throw new InvalidOperationException("The analysis output is missing the centre, settings, or edge data required for Scene inspection.");
        var hours = parsed.edges[0].hourly?.Select(value => value.hour).ToArray() ?? Array.Empty<int>();
        if (hours.Length == 0 || parsed.edges.Any(edge => edge.coordinates == null || edge.coordinates.Length < 2 || edge.hourly == null || !edge.hourly.Select(value => value.hour).SequenceEqual(hours)))
            throw new InvalidOperationException("Every edge must have coordinates and the same hourly slices.");
        document = parsed;
        loadedPath = path;
        selectedHourIndex = 0;
        selectedEdgeIndex = 0;
        lastFocusedEdgeIndex = -1;
        displayLimit = Math.Min(Math.Max(1000, document.edges.Count), 200000);
        lastInspection = null;
        FocusSelectedEdge();
        SceneView.RepaintAll();
        Repaint();
    }

    private void FocusSelectedEdge()
    {
        if (document == null || selectedEdgeIndex == lastFocusedEdgeIndex ||
            selectedEdgeIndex < 0 || selectedEdgeIndex >= document.edges.Count) return;
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null) return;

        var edge = document.edges[selectedEdgeIndex];
        if (edge.coordinates == null || edge.coordinates.Length < 2) return;
        using var localReference = CreateLocalReference();
        var points = edge.coordinates.Select(coordinate => Project(localReference, coordinate, 2.0f)).ToArray();
        var center = points.Aggregate(Vector3.zero, (sum, point) => sum + point) / points.Length;
        var spanMeters = points.Max(point => Vector3.Distance(point, center)) * 2.0f;
        var viewSize = Mathf.Clamp(Mathf.Max(16.0f, spanMeters * 2.5f), 16.0f, 250.0f);

        sceneView.LookAt(center, sceneView.rotation, viewSize, sceneView.orthographic, true);
        sceneView.Repaint();
        lastFocusedEdgeIndex = selectedEdgeIndex;
    }

    private void DrawScene(SceneView sceneView)
    {
        if (document == null) return;
        var hours = AvailableHours();
        if (selectedHourIndex < 0 || selectedHourIndex >= hours.Length) return;
        var selectedHour = hours[selectedHourIndex];
        using var localReference = CreateLocalReference();
        foreach (var edge in document.edges.Take(displayLimit))
        {
            Handles.color = HeatmapColor(edge.hourly.First(value => value.hour == selectedHour));
            DrawEdge(localReference, edge, lineWidth, 2.0f);
        }

        var selectedEdge = document.edges[selectedEdgeIndex];
        Handles.color = new Color(0.76f, 0.42f, 0.97f, 1.0f);
        DrawEdge(localReference, selectedEdge, lineWidth + 2.0f, 2.5f);
        if (drawSelectedEdgeSamples) DrawSelectedEdgeSamples(localReference, selectedEdge, selectedHour);
    }

    private GeoReference CreateLocalReference()
    {
        using var worldReference = GeoReference.Create(new PlateauVector3d(0.0, 0.0, 0.0), 1.0f, CoordinateSystem.EUN, document.coordinateZoneId);
        var referencePoint = worldReference.Project(new GeoCoordinate(document.center[1], document.center[0], 0.0));
        return GeoReference.Create(referencePoint, 1.0f, CoordinateSystem.EUN, document.coordinateZoneId);
    }

    private static void DrawEdge(GeoReference localReference, HeatmapEdge edge, float width, float height)
    {
        var points = edge.coordinates.Select(coordinate => Project(localReference, coordinate, height)).ToArray();
        if (points.Length >= 2) Handles.DrawAAPolyLine(width, points);
    }

    private void DrawSelectedEdgeSamples(GeoReference localReference, HeatmapEdge edge, int hour)
    {
        if (!TryGetSun(hour, out var sun)) return;
        var from = edge.coordinates[0];
        var to = edge.coordinates[edge.coordinates.Length - 1];
        var lengthMeters = DistanceMeters(from[1], from[0], to[1], to[0]);
        var subdivisions = Math.Max(1, (int)Math.Ceiling(lengthMeters / document.settings.sampleSpacingMeters));
        var totalSamples = subdivisions + 1;
        var sampleStride = Math.Max(1, (int)Math.Ceiling(totalSamples / (double)sampleDrawLimit));
        var inspection = new SampleInspection
        {
            totalSamples = CountSamplesWithinCoverage(from, to, subdivisions),
            azimuthDegrees = sun.azimuthDegrees,
            elevationDegrees = sun.elevationDegrees
        };
        var buildingMask = 1 << BuildingLayer;
        var roadMask = 1 << RoadLayer;

        for (var sampleIndex = 0; sampleIndex <= subdivisions; sampleIndex += sampleStride)
        {
            var ratio = sampleIndex / (double)subdivisions;
            var coordinate = new[] { Lerp(from[0], to[0], ratio), Lerp(from[1], to[1], ratio) };
            if (DistanceMeters(document.center[1], document.center[0], coordinate[1], coordinate[0]) > document.radiusMeters) continue;
            var projected = Project(localReference, coordinate, 0.0f);
            var rayOrigin = new Vector3(projected.x, 500.0f, projected.z);
            inspection.drawnSamples++;
            if (!Physics.Raycast(rayOrigin, Vector3.down, out var roadHit, 1000.0f, roadMask, QueryTriggerInteraction.Ignore))
            {
                inspection.noGroundSamples++;
                DrawSampleMarker(projected + Vector3.up, new Color(0.88f, 0.20f, 0.25f, 1.0f));
                continue;
            }
            var pedestrianPoint = roadHit.point + Vector3.up * (float)document.settings.pedestrianHeightMeters;
            var shaded = sun.elevationDegrees > 0.0 && Physics.Raycast(pedestrianPoint, sun.direction, 10000.0f, buildingMask, QueryTriggerInteraction.Ignore);
            if (shaded)
            {
                inspection.shadedSamples++;
                DrawSampleMarker(pedestrianPoint, new Color(0.10f, 0.72f, 0.35f, 1.0f));
            }
            else
            {
                inspection.sunlitSamples++;
                DrawSampleMarker(pedestrianPoint, new Color(0.96f, 0.57f, 0.10f, 1.0f));
            }
        }

        var midpoint = Project(localReference, new[] { (from[0] + to[0]) / 2.0, (from[1] + to[1]) / 2.0 }, 3.0f);
        DrawSunArrow(midpoint, sun);
        lastInspection = inspection;
    }

    private static void DrawSampleMarker(Vector3 point, Color color)
    {
        Handles.color = color;
        Handles.SphereHandleCap(0, point, Quaternion.identity, SampleMarkerSize, EventType.Repaint);
    }

    private static void DrawSunArrow(Vector3 origin, HourlyEnvironmentCostRules.SunPosition sun)
    {
        const float arrowLength = 12.0f;
        Handles.color = new Color(0.69f, 0.45f, 0.95f, 1.0f);
        Handles.DrawAAPolyLine(3.0f, origin, origin + sun.direction * arrowLength);
        Handles.ArrowHandleCap(0, origin + sun.direction * arrowLength, Quaternion.LookRotation(sun.direction), 3.5f, EventType.Repaint);
        Handles.Label(origin + Vector3.up * 1.5f, $"太陽: 方位 {sun.azimuthDegrees:F1}°, 高度 {sun.elevationDegrees:F1}°");
    }

    private bool TryGetSun(int hour, out HourlyEnvironmentCostRules.SunPosition sun)
    {
        if (!DateTime.TryParseExact(document.settings.date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Debug.LogWarning($"Cannot inspect solar direction because settings.date is invalid: {document.settings.date}");
            sun = default;
            return false;
        }
        sun = HourlyEnvironmentCostRules.CalculateSun(date, hour, document.center[1], document.center[0], document.settings.timezone);
        return true;
    }

    private int[] AvailableHours() => document.edges[0].hourly.Select(value => value.hour).ToArray();
    private static Vector3 Project(GeoReference localReference, double[] coordinate, float height)
    {
        var projected = localReference.Project(new GeoCoordinate(coordinate[1], coordinate[0], 0.0));
        return new Vector3((float)projected.X, height, (float)projected.Z);
    }
    private static Color HeatmapColor(HeatmapHourly value)
    {
        if (value.status == "missing" || !value.shadeRatio.HasValue) return new Color(0.39f, 0.45f, 0.55f, 0.9f);
        var orange = new Color(0.96f, 0.62f, 0.04f, 0.95f);
        var green = new Color(0.02f, 0.47f, 0.34f, 0.95f);
        var color = Color.Lerp(orange, green, Mathf.Clamp01((float)value.shadeRatio.Value));
        if (value.status == "partial") color.a = 0.65f;
        return color;
    }
    private static string FormatRatio(double? value) => value.HasValue ? value.Value.ToString("P1", CultureInfo.InvariantCulture) : "n/a";
    private static double Lerp(double from, double to, double ratio) => from + (to - from) * ratio;
    private int CountSamplesWithinCoverage(double[] from, double[] to, int subdivisions)
    {
        var count = 0;
        for (var sampleIndex = 0; sampleIndex <= subdivisions; sampleIndex++)
        {
            var ratio = sampleIndex / (double)subdivisions;
            var latitude = Lerp(from[1], to[1], ratio);
            var longitude = Lerp(from[0], to[0], ratio);
            if (DistanceMeters(document.center[1], document.center[0], latitude, longitude) <= document.radiusMeters) count++;
        }
        return count;
    }
    private static double DistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double earthRadiusMeters = 6371008.8;
        var latitudeARadians = latitudeA * Math.PI / 180.0;
        var latitudeBRadians = latitudeB * Math.PI / 180.0;
        var latitudeDelta = (latitudeB - latitudeA) * Math.PI / 180.0;
        var longitudeDelta = (longitudeB - longitudeA) * Math.PI / 180.0;
        var sinLatitude = Math.Sin(latitudeDelta / 2.0);
        var sinLongitude = Math.Sin(longitudeDelta / 2.0);
        var haversine = sinLatitude * sinLatitude + Math.Cos(latitudeARadians) * Math.Cos(latitudeBRadians) * sinLongitude * sinLongitude;
        return earthRadiusMeters * 2.0 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1.0 - haversine));
    }
    private static string DefaultOutputDirectory()
    {
        var current = Directory.GetParent(Application.dataPath)?.Parent;
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) return Path.Combine(current.FullName, "data", "generated");
            current = current.Parent;
        }
        return Application.dataPath;
    }

    [Serializable] private sealed class HeatmapDocument { public string schemaVersion; public string status; public string areaId; public double[] center; public double radiusMeters; public int coordinateZoneId; public HeatmapSettings settings; public List<HeatmapEdge> edges; }
    [Serializable] private sealed class HeatmapSettings { public string date; public string timezone; public double sampleSpacingMeters; public double pedestrianHeightMeters; }
    [Serializable] private sealed class HeatmapEdge { public string id; public double[][] coordinates; public int sampleCount; public int validSampleCount; public int noGroundSampleCount; public HeatmapHourly[] hourly; }
    [Serializable] private sealed class HeatmapHourly { public int hour; public string status; public double? shadeRatio; }
    private sealed class SampleInspection { public int totalSamples; public int drawnSamples; public int shadedSamples; public int sunlitSamples; public int noGroundSamples; public double azimuthDegrees; public double elevationDegrees; }
}
