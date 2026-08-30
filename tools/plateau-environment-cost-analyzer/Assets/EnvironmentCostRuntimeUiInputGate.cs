using UnityEngine.UIElements;

/// <summary>Shared input boundary between Runtime UI Toolkit panels and map/camera interaction.</summary>
public static class EnvironmentCostRuntimeUiInputGate
{
    public static bool IsPointerOverUi { get; private set; }
    public static bool IsTextInputFocused { get; private set; }

    public static void Track(VisualElement panel)
    {
        panel.RegisterCallback<PointerEnterEvent>(_ => IsPointerOverUi = true);
        panel.RegisterCallback<PointerLeaveEvent>(_ => IsPointerOverUi = false);
        panel.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        panel.schedule.Execute(() =>
        {
            var focused = panel.panel?.focusController?.focusedElement;
            IsTextInputFocused = focused is TextField;
        }).Every(100);
    }
}
