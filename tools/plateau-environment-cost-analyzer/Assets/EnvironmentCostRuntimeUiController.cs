using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Creates the Runtime UIDocument and composes controller-owned UI Toolkit panels.</summary>
public sealed class EnvironmentCostRuntimeUiController : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToLegacyInspectionScene()
    {
        var metadata = FindFirstObjectByType<EnvironmentCostInspectionMetadata>();
        if (metadata != null && metadata.GetComponent<EnvironmentCostRuntimeUiController>() == null)
            metadata.gameObject.AddComponent<EnvironmentCostRuntimeUiController>();
    }

    private IEnumerator Start()
    {
        yield return null;
        var solar = GetComponent<EnvironmentCostSolarController>();
        var shade = GetComponent<EnvironmentCostRuntimeShadeAnalysisController>();
        var policy = GetComponent<EnvironmentCostRuntimePolicyScenarioController>();
        while (solar == null || shade == null || policy == null || policy.Scenario == null)
        {
            solar ??= GetComponent<EnvironmentCostSolarController>();
            shade ??= GetComponent<EnvironmentCostRuntimeShadeAnalysisController>();
            policy ??= GetComponent<EnvironmentCostRuntimePolicyScenarioController>();
            yield return null;
        }

        var panelSettings = Resources.Load<PanelSettings>("EnvironmentCostRuntimePanelSettings");
        if (panelSettings == null)
        {
            Debug.LogError("Runtime UI PanelSettings asset is missing. Create EnvironmentCostRuntimePanelSettings.asset before building the Player.");
            yield break;
        }
        var uiObject = new GameObject("Environment Cost Runtime UI");
        var document = uiObject.AddComponent<UIDocument>();
        document.panelSettings = panelSettings;
        document.visualTreeAsset = Resources.Load<VisualTreeAsset>("EnvironmentCostRuntimeUi");
        var root = document.rootVisualElement.Q<VisualElement>("runtime-ui-root") ?? document.rootVisualElement;
        // A PanelSettings created for Runtime has no editor-only default font. Assign an OS font
        // explicitly so Japanese labels and controls remain visible in a standalone Player.
        root.style.unityFont = Font.CreateDynamicFontFromOSFont(new[] { "Yu Gothic UI", "Meiryo UI", "Arial" }, 12);
        // The first UXML revision contains declarative design-time examples. Runtime controllers
        // construct and bind the live controls below, so clear those examples to avoid rendering
        // a second set of panels while the binding is migrated into UXML.
        root.Clear();
        var style = Resources.Load<StyleSheet>("EnvironmentCostRuntimeUi");
        if (style != null) root.styleSheets.Add(style);
        solar.BuildUi(root);
        shade.BuildUi(root);
        policy.BuildUi(root);
    }
}
