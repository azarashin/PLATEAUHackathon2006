using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Shared input boundary between Runtime UI Toolkit panels and map/camera interaction.</summary>
public static class EnvironmentCostRuntimeUiInputGate
{
    public static bool IsPointerOverUi { get; private set; }
    public static bool IsTextInputFocused { get; private set; }
    private static VisualElement trackedDocumentRoot;
    private static VisualElement trackedUiSurface;
    private static Focusable focusedElement;
    private static bool editableFieldWasPointerSelected;

    /// <summary>Tracks the full UIDocument input boundary and its visible UI surface.</summary>
    public static void TrackDocument(VisualElement documentRoot, VisualElement uiSurface)
    {
        if (trackedDocumentRoot != null) StopTracking(trackedDocumentRoot);
        trackedDocumentRoot = documentRoot;
        trackedUiSurface = uiSurface;
        editableFieldWasPointerSelected = false;
        IsPointerOverUi = false;
        IsTextInputFocused = false;

        uiSurface.RegisterCallback<PointerEnterEvent>(OnUiPointerEnter);
        uiSurface.RegisterCallback<PointerLeaveEvent>(OnUiPointerLeave);
        uiSurface.RegisterCallback<PointerDownEvent>(StopUiPointerPropagation);

        // When no control has focus, Runtime UI sends keyboard/navigation events to the
        // UIDocument's visualTree rather than a child runtime-panel. Register at this top-level
        // boundary so the first W/S/A/D press cannot focus the first TextField automatically.
        documentRoot.RegisterCallback<PointerDownEvent>(OnDocumentPointerDown, TrickleDown.TrickleDown);
        documentRoot.RegisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
        documentRoot.RegisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);
        // W/S/A/D are mapped to UI navigation by the legacy Input Manager.  Do not let that
        // navigation move focus from a slider/button into a nearby TextField; these keys belong
        // to the inspection fly camera unless an editable field was explicitly pointer-selected.
        documentRoot.RegisterCallback<KeyDownEvent>(OnDocumentKeyDown, TrickleDown.TrickleDown);
        // The legacy Input Manager also converts W/S/A/D and the arrow keys into a
        // NavigationMoveEvent. Stopping only KeyDownEvent therefore still lets UI Toolkit
        // move focus or modify a focused slider. Runtime UI is pointer-first, so navigation
        // movement is disabled while direct keyboard entry into editable fields remains valid.
        documentRoot.RegisterCallback<NavigationMoveEvent>(OnNavigationMove, TrickleDown.TrickleDown);
    }

    /// <summary>Unregisters callbacks when the owning Runtime UI is destroyed or replaced.</summary>
    public static void StopTracking(VisualElement documentRoot)
    {
        if (documentRoot == null || trackedDocumentRoot != documentRoot) return;

        documentRoot.UnregisterCallback<PointerDownEvent>(OnDocumentPointerDown, TrickleDown.TrickleDown);
        documentRoot.UnregisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
        documentRoot.UnregisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);
        documentRoot.UnregisterCallback<KeyDownEvent>(OnDocumentKeyDown, TrickleDown.TrickleDown);
        documentRoot.UnregisterCallback<NavigationMoveEvent>(OnNavigationMove, TrickleDown.TrickleDown);
        trackedUiSurface?.UnregisterCallback<PointerEnterEvent>(OnUiPointerEnter);
        trackedUiSurface?.UnregisterCallback<PointerLeaveEvent>(OnUiPointerLeave);
        trackedUiSurface?.UnregisterCallback<PointerDownEvent>(StopUiPointerPropagation);

        ClearTextInputFocus();
        trackedDocumentRoot = null;
        trackedUiSurface = null;
        IsPointerOverUi = false;
    }

    /// <summary>
    /// Keeps mouse-operated controls out of UI Toolkit's keyboard focus ring. Text and numeric
    /// fields remain focusable so they can still be edited after an explicit pointer click.
    /// </summary>
    public static void DisableNonEditableKeyboardFocus(VisualElement root)
    {
        root.Query<Button>().ForEach(DisableKeyboardFocus);
        root.Query<Slider>().ForEach(DisableKeyboardFocus);
        root.Query<SliderInt>().ForEach(DisableKeyboardFocus);
        root.Query<Toggle>().ForEach(DisableKeyboardFocus);
        // Keep editable fields clickable but remove them from directional/tab navigation.
        // They become keyboard targets only after a direct pointer click.
        root.Query<TextField>().ForEach(DisableNavigationFocus);
        root.Query<FloatField>().ForEach(DisableNavigationFocus);
    }

    /// <summary>Returns keyboard ownership to map/camera input after a click outside Runtime UI.</summary>
    public static void ClearTextInputFocus()
    {
        editableFieldWasPointerSelected = false;
        var liveFocusedElement = trackedDocumentRoot?.panel?.focusController?.focusedElement;
        liveFocusedElement?.Blur();
        if (focusedElement != liveFocusedElement) focusedElement?.Blur();
        focusedElement = null;
        IsTextInputFocused = false;
    }

    private static bool IsCameraKey(KeyCode keyCode) => keyCode == KeyCode.W || keyCode == KeyCode.A || keyCode == KeyCode.S ||
        keyCode == KeyCode.D || keyCode == KeyCode.Q || keyCode == KeyCode.E;

    /// <summary>Applies pointer ownership using an element resolved either by UI events or panel picking.</summary>
    public static void HandlePointerSelection(VisualElement target, bool isPointerOverUi)
    {
        IsPointerOverUi = isPointerOverUi;
        editableFieldWasPointerSelected = IsEditableField(target);
        if (!editableFieldWasPointerSelected)
        {
            ClearTextInputFocus();
            return;
        }

        focusedElement = target;
        IsTextInputFocused = true;
    }

    private static void OnUiPointerEnter(PointerEnterEvent evt) => IsPointerOverUi = true;
    private static void OnUiPointerLeave(PointerLeaveEvent evt) => IsPointerOverUi = false;
    private static void StopUiPointerPropagation(PointerDownEvent evt) => evt.StopPropagation();
    private static void OnDocumentPointerDown(PointerDownEvent evt)
    {
        var target = evt.target as VisualElement;
        HandlePointerSelection(target, IsInsideTrackedUi(target));
    }

    private static bool IsInsideTrackedUi(VisualElement target)
    {
        for (var current = target; current != null; current = current.parent)
            if (current == trackedUiSurface) return true;
        return false;
    }

    private static void OnFocusIn(FocusInEvent evt)
    {
        focusedElement = evt.target as Focusable;
        IsTextInputFocused = editableFieldWasPointerSelected && IsEditableField(focusedElement);
        if (IsEditableField(focusedElement) && !editableFieldWasPointerSelected) focusedElement.Blur();
    }

    private static void OnFocusOut(FocusOutEvent evt)
    {
        focusedElement = null;
        IsTextInputFocused = false;
    }

    private static void OnDocumentKeyDown(KeyDownEvent evt)
    {
        if (!IsCameraKey(evt.keyCode) || CanTypeCameraKey()) return;
        evt.StopImmediatePropagation();
        evt.PreventDefault();
    }

    private static void OnNavigationMove(NavigationMoveEvent evt)
    {
        evt.PreventDefault();
        evt.StopImmediatePropagation();
    }

    private static bool CanTypeCameraKey()
    {
        var liveFocusedElement = trackedDocumentRoot?.panel?.focusController?.focusedElement ?? focusedElement;
        return editableFieldWasPointerSelected && IsEditableField(liveFocusedElement);
    }

    private static bool IsEditableField(Focusable focused)
    {
        var element = focused as VisualElement;
        return element is TextField || element is FloatField ||
               element?.GetFirstAncestorOfType<TextField>() != null ||
               element?.GetFirstAncestorOfType<FloatField>() != null;
    }

    private static void DisableKeyboardFocus(Focusable control)
    {
        control.focusable = false;
        control.tabIndex = -1;
    }

    private static void DisableNavigationFocus(Focusable control) => control.tabIndex = -1;
}
