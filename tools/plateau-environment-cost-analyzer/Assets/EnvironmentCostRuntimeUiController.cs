using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Creates the Runtime UIDocument and composes controller-owned UI Toolkit panels.</summary>
public sealed class EnvironmentCostRuntimeUiController : MonoBehaviour
{
    private VisualElement runtimeDocumentRoot;
    private VisualElement runtimeUiSurface;

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
        runtimeDocumentRoot = document.rootVisualElement;
        var root = runtimeDocumentRoot.Q<VisualElement>("runtime-ui-root") ?? runtimeDocumentRoot;
        runtimeUiSurface = root;
        // A PanelSettings created for Runtime has no editor-only default font. Assign an OS font
        // explicitly so Japanese labels and controls remain visible in a standalone Player.
        root.style.unityFont = Font.CreateDynamicFontFromOSFont(new[] { "Yu Gothic UI", "Meiryo UI", "Arial" }, 11);
        // The first UXML revision contains declarative design-time examples. Runtime controllers
        // construct and bind the live controls below, so clear those examples to avoid rendering
        // a second set of panels while the binding is migrated into UXML.
        root.Clear();
        EnvironmentCostRuntimeUiInputGate.TrackDocument(runtimeDocumentRoot, root);
        var style = Resources.Load<StyleSheet>("EnvironmentCostRuntimeUi");
        if (style != null) root.styleSheets.Add(style);
        var tabs = new VisualElement(); tabs.AddToClassList("runtime-tabs"); root.Add(tabs);
        var content = new VisualElement(); content.AddToClassList("runtime-tab-content"); root.Add(content);
        solar.BuildUi(content);
        shade.BuildUi(content);
        policy.BuildUi(content);

        var panels = new[] { content.ElementAt(0), content.ElementAt(1), content.ElementAt(2) };
        var tabButtons = new Button[panels.Length];
        void SelectTab(int selectedIndex)
        {
            for (var index = 0; index < panels.Length; index++)
            {
                panels[index].style.display = index == selectedIndex ? DisplayStyle.Flex : DisplayStyle.None;
                tabButtons[index].EnableInClassList("runtime-tab-active", index == selectedIndex);
            }
        }
        var labels = new[] { "太陽・影", "日陰解析", "施策シナリオ" };
        for (var index = 0; index < labels.Length; index++)
        {
            var captured = index;
            tabButtons[index] = new Button(() => SelectTab(captured)) { text = labels[index] };
            tabButtons[index].AddToClassList("runtime-tab");
            tabs.Add(tabButtons[index]);
        }
        EnvironmentCostRuntimeUiInputGate.DisableNonEditableKeyboardFocus(root);
        SelectTab(0);
        EnvironmentCostRuntimeUiInputGate.ClearTextInputFocus();
        yield return null;
        if (!EnvironmentCostRuntimeUiInputGate.IsTextInputFocused)
            EnvironmentCostRuntimeUiInputGate.ClearTextInputFocus();
    }

    private void Update()
    {
        if (!Input.GetMouseButtonDown(0) || runtimeDocumentRoot?.panel == null || runtimeUiSurface == null) return;

        var screenPosition = (Vector2)Input.mousePosition;
        screenPosition.y = Screen.height - screenPosition.y;
        var panelPosition = RuntimePanelUtils.ScreenToPanel(runtimeDocumentRoot.panel, screenPosition);
        var isPointerOverUi = runtimeUiSurface.worldBound.Contains(panelPosition);
        var target = isPointerOverUi
            ? runtimeDocumentRoot.panel.Pick(panelPosition)
            : null;
        EnvironmentCostRuntimeUiInputGate.HandlePointerSelection(target, isPointerOverUi);
    }

    private void OnDestroy()
    {
        if (runtimeDocumentRoot != null)
            EnvironmentCostRuntimeUiInputGate.StopTracking(runtimeDocumentRoot);
    }
}
