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

        var uiObject = new GameObject("Environment Cost Runtime UI");
        var document = uiObject.AddComponent<UIDocument>();
        document.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        document.visualTreeAsset = Resources.Load<VisualTreeAsset>("EnvironmentCostRuntimeUi");
        var root = document.rootVisualElement.Q<VisualElement>("runtime-ui-root") ?? document.rootVisualElement;
        var style = Resources.Load<StyleSheet>("EnvironmentCostRuntimeUi");
        if (style != null) root.styleSheets.Add(style);
        solar.BuildUi(root);
        shade.BuildUi(root);
        policy.BuildUi(root);
    }
}
