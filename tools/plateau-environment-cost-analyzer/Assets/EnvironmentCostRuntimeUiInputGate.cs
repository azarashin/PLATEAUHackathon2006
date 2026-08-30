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
        panel.schedule.Execute(() =>
        {
            focusedElement = panel.panel?.focusController?.focusedElement;
            IsTextInputFocused = focusedElement is TextField;
        }).Every(100);
    }

    /// <summary>Returns keyboard ownership to map/camera input after a click outside Runtime UI.</summary>
    public static void ClearTextInputFocus()
    {
        focusedElement?.Blur();
        focusedElement = null;
        IsTextInputFocused = false;
    }
}
