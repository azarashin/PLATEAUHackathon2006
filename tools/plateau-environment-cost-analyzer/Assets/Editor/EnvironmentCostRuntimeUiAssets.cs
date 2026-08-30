using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Creates the persistent UI Toolkit assets required by Runtime inspection Players.</summary>
public static class EnvironmentCostRuntimeUiAssets
{
    public const string PanelSettingsPath = "Assets/Resources/EnvironmentCostRuntimePanelSettings.asset";

    [MenuItem("PLATEAU/Environment Cost/Create Runtime UI Toolkit Assets")]
    public static void CreateFromMenu() => Ensure();

    public static void Run()
    {
        try { Ensure(); Debug.Log("ENVIRONMENT_COST_RUNTIME_UI_ASSETS_READY"); EditorApplication.Exit(0); }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }

    public static PanelSettings Ensure()
    {
        var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
        if (existing != null) return existing;
        var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        AssetDatabase.CreateAsset(panelSettings, PanelSettingsPath);
        AssetDatabase.SaveAssets();
        return panelSettings;
    }
}
