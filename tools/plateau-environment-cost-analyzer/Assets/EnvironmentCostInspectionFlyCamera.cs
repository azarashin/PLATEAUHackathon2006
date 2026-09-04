using UnityEngine;

/// <summary>Simple runtime camera for locally generated environment-cost inspection Scenes.</summary>
public sealed class EnvironmentCostInspectionFlyCamera : MonoBehaviour
{
    [SerializeField] private float movementMetersPerSecond = 40f;
    [SerializeField] private float lookDegreesPerPixel = 0.15f;

    private float yaw;
    private float pitch;
    private EnvironmentCostRuntimeShadeAnalysisController shadeAnalysis;

    private void Awake()
    {
        var angles = transform.eulerAngles;
        yaw = angles.y;
        pitch = angles.x;
        shadeAnalysis = FindFirstObjectByType<EnvironmentCostRuntimeShadeAnalysisController>();
    }

    private void Update()
    {
        // UI Toolkit focus owns keyboard input while text is being edited.
        shadeAnalysis ??= FindFirstObjectByType<EnvironmentCostRuntimeShadeAnalysisController>();
        if (!Application.isPlaying || EnvironmentCostRuntimeUiInputGate.IsTextInputFocused || shadeAnalysis?.IsRunning == true) return;
        var speed = movementMetersPerSecond * (Input.GetKey(KeyCode.LeftShift) ? 3f : 1f) * Time.deltaTime;
        var movement = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        if (Input.GetKey(KeyCode.E)) movement.y += 1f;
        if (Input.GetKey(KeyCode.Q)) movement.y -= 1f;
        transform.position += transform.TransformDirection(movement.normalized) * speed;

        // A right-click over Runtime UI must not rotate the scene behind it. Keyboard movement
        // remains available unless an editable field owns text input.
        if (!Input.GetMouseButton(1) || EnvironmentCostRuntimeUiInputGate.IsPointerOverUi) return;
        yaw += Input.GetAxis("Mouse X") * lookDegreesPerPixel * 100f;
        pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * lookDegreesPerPixel * 100f, -85f, 85f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }
}
