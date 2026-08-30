using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Shared input boundary between Runtime UI Toolkit panels and map/camera interaction.</summary>
public static class EnvironmentCostRuntimeUiInputGate
{
    public static bool IsPointerOverUi { get; private set; }
    public static bool IsTextInputFocused { get; private set; }
    private static Focusable focusedElement;

    public static void Track(VisualElement panel)
    {
        panel.RegisterCallback<PointerEnterEvent>(_ => IsPointerOverUi = true);
        panel.RegisterCallback<PointerLeaveEvent>(_ => IsPointerOverUi = false);
        panel.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        // W/S/A/D are mapped to UI navigation by the legacy Input Manager.  Do not let that
        // navigation move focus from a slider/button into a nearby TextField; these keys belong
        // to the inspection fly camera unless a TextField itself has focus.
        panel.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (!IsCameraKey(evt.keyCode) || HasTextFieldFocus(panel)) return;
            evt.StopImmediatePropagation();
            evt.PreventDefault();
        }, TrickleDown.TrickleDown);
        panel.schedule.Execute(() =>
        {
            focusedElement = panel.panel?.focusController?.focusedElement;
            IsTextInputFocused = IsTextField(focusedElement);
        }).Every(100);
    }

    /// <summary>Returns keyboard ownership to map/camera input after a click outside Runtime UI.</summary>
    public static void ClearTextInputFocus()
    {
        focusedElement?.Blur();
        focusedElement = null;
        IsTextInputFocused = false;
    }

    private static bool IsCameraKey(KeyCode keyCode) => keyCode == KeyCode.W || keyCode == KeyCode.A || keyCode == KeyCode.S ||
        keyCode == KeyCode.D || keyCode == KeyCode.Q || keyCode == KeyCode.E;

    private static bool HasTextFieldFocus(VisualElement panel) => IsTextField(panel.panel?.focusController?.focusedElement);

    private static bool IsTextField(Focusable focused)
    {
        var element = focused as VisualElement;
        return element is TextField || element?.GetFirstAncestorOfType<TextField>() != null;
    }
}
