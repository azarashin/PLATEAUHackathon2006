using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Drives the inspection Scene's directional light from the same solar-position calculation as the batch analyser.
/// It intentionally shows only a visual time simulation: it never changes the analysed hourly JSON.
/// </summary>
public sealed class EnvironmentCostSolarController : MonoBehaviour
{
    [SerializeField] private EnvironmentCostInspectionMetadata metadata;
    [SerializeField] private Light directionalLight;
    [SerializeField] private float firstHour = 8f;
    [SerializeField] private float lastHour = 17f;
    [SerializeField] private float localHour = 12f;
    [SerializeField] private float shadowDistanceMeters = 250f;
    [SerializeField] private string dateText;

    private string validationMessage;
    private HourlyEnvironmentCostRules.SunPosition currentSun;

    public void Configure(EnvironmentCostInspectionMetadata newMetadata, Light newDirectionalLight, int[] analysisHours)
    {
        metadata = newMetadata;
        directionalLight = newDirectionalLight;
        if (analysisHours != null && analysisHours.Length > 0)
        {
            firstHour = analysisHours[0];
            lastHour = analysisHours[analysisHours.Length - 1];
        }
        localHour = Mathf.Clamp(12f, firstHour, lastHour);
        dateText = metadata != null ? metadata.AnalysisDate : dateText;
        ApplySun();
    }

    private void Start()
    {
        if (string.IsNullOrWhiteSpace(dateText) && metadata != null) dateText = metadata.AnalysisDate;
        QualitySettings.shadowDistance = Mathf.Max(0f, shadowDistanceMeters);
        ApplySun();
    }

    private void OnGUI()
    {
        if (!Application.isPlaying) return;

        const float width = 340f;
        GUILayout.BeginArea(new Rect(16f, 16f, width, 172f), GUI.skin.box);
        GUILayout.Label("太陽・影の確認", GUI.skin.label);
        GUILayout.Label($"地域: {metadata?.AreaId ?? "未設定"}  タイムゾーン: {metadata?.Timezone ?? "未設定"}");
        GUILayout.BeginHorizontal();
        GUILayout.Label("日付 (YYYY-MM-DD)", GUILayout.Width(145f));
        var updatedDate = GUILayout.TextField(dateText ?? string.Empty, GUILayout.Width(150f));
        GUILayout.EndHorizontal();
        if (!string.Equals(updatedDate, dateText, StringComparison.Ordinal))
        {
            dateText = updatedDate;
            ApplySun();
        }

        GUILayout.Label($"時刻: {localHour:00.00} {metadata?.Timezone ?? "未設定"}");
        var updatedHour = GUILayout.HorizontalSlider(localHour, firstHour, lastHour);
        if (!Mathf.Approximately(updatedHour, localHour))
        {
            localHour = updatedHour;
            ApplySun();
        }

        if (!string.IsNullOrWhiteSpace(validationMessage)) GUILayout.Label(validationMessage);
        else if (currentSun.elevationDegrees <= 0.0) GUILayout.Label("夜間: ディレクショナルライトを無効化（解析用の日陰値にはしません）");
        else GUILayout.Label($"方位: {currentSun.azimuthDegrees:F1}°  高度: {currentSun.elevationDegrees:F1}°");
        GUILayout.Label($"影の可視化範囲: カメラから約{shadowDistanceMeters:F0} m");
        GUILayout.EndArea();
    }

    private void ApplySun()
    {
        if (metadata == null || directionalLight == null)
        {
            validationMessage = "太陽表示の設定が不足しています。";
            return;
        }
        if (!DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            validationMessage = "日付は YYYY-MM-DD で入力してください。";
            directionalLight.enabled = false;
            return;
        }

        try
        {
            currentSun = HourlyEnvironmentCostRules.CalculateSun(date, localHour, metadata.Latitude,
                metadata.Longitude, metadata.Timezone);
            directionalLight.transform.rotation = Quaternion.LookRotation(-currentSun.direction, Vector3.up);
            directionalLight.enabled = currentSun.elevationDegrees > 0.0;
            validationMessage = null;
        }
        catch (ArgumentException exception)
        {
            validationMessage = exception.Message;
            directionalLight.enabled = false;
        }
    }
}
