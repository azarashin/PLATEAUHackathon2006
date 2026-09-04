using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Drives the inspection Scene's directional light from the same solar-position calculation as the batch analyser.
/// It intentionally shows only a visual time simulation: it never changes the analysed hourly JSON.
/// </summary>
public sealed class EnvironmentCostSolarController : MonoBehaviour
{
    private const float MinimumVisualizationShadowDistanceMeters = 1500f;
    private const float MaximumVisualizationShadowDistanceMeters = 3000f;
    [SerializeField] private EnvironmentCostInspectionMetadata metadata;
    [SerializeField] private Light directionalLight;
    [SerializeField] private float firstHour = 8f;
    [SerializeField] private float lastHour = 17f;
    [SerializeField] private float localHour = 12f;
    [SerializeField] private float shadowDistanceMeters = MinimumVisualizationShadowDistanceMeters;
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
        ApplyVisualizationShadowQuality();
        ApplySun();
    }

    /// <summary>
    /// Applies display-only shadow settings for inspecting a wide city model.
    /// These settings never affect hourly analysis JSON or route-cost data.
    /// </summary>
    private void ApplyVisualizationShadowQuality()
    {
        shadowDistanceMeters = ResolveVisualizationShadowDistanceMeters(shadowDistanceMeters, metadata?.RadiusMeters ?? 0f);
        QualitySettings.shadowDistance = shadowDistanceMeters;
        QualitySettings.shadowResolution = ShadowResolution.Low;
        QualitySettings.shadowCascades = 1;
        if (directionalLight != null) directionalLight.shadows = LightShadows.Hard;
    }

    /// <summary>Chooses a low-cost distance that keeps wide-view shadows visible without unbounded quality cost.</summary>
    public static float ResolveVisualizationShadowDistanceMeters(float configuredMeters, float packageRadiusMeters)
    {
        var requestedMeters = Mathf.Max(configuredMeters, packageRadiusMeters);
        return Mathf.Clamp(requestedMeters, MinimumVisualizationShadowDistanceMeters, MaximumVisualizationShadowDistanceMeters);
    }

    public void BuildUi(VisualElement root)
    {
        var panel = new VisualElement(); panel.AddToClassList("runtime-panel"); root.Add(panel);
        var title = new Label("太陽・影の確認"); title.AddToClassList("runtime-panel-title"); panel.Add(title);
        panel.Add(new Label($"地域: {metadata?.AreaId ?? "未設定"}  タイムゾーン: {metadata?.Timezone ?? "未設定"}"));
        var date = new TextField("日付 (YYYY-MM-DD)") { value = dateText ?? string.Empty }; panel.Add(date);
        date.RegisterValueChangedCallback(change => { dateText = change.newValue; ApplySun(); });
        var hour = new Slider("時刻", firstHour, lastHour) { value = localHour }; panel.Add(hour);
        var details = new Label(); details.AddToClassList("runtime-status"); panel.Add(details);
        hour.RegisterValueChangedCallback(change => { localHour = change.newValue; ApplySun(); });
        panel.schedule.Execute(() => details.text = !string.IsNullOrWhiteSpace(validationMessage) ? validationMessage :
            currentSun.elevationDegrees <= 0.0 ? "夜間: ディレクショナルライトを無効化" :
            $"時刻: {localHour:00.00} {metadata?.Timezone}\n方位: {currentSun.azimuthDegrees:F1}°  高度: {currentSun.elevationDegrees:F1}°\n影の可視化範囲: カメラから約{shadowDistanceMeters:F0} m").Every(100);
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
