using UnityEngine;

/// <summary>Simple runtime camera for locally generated environment-cost inspection Scenes.</summary>
public sealed class EnvironmentCostInspectionFlyCamera : MonoBehaviour
{
    [SerializeField] private float movementMetersPerSecond = 40f;
    [SerializeField] private float lookDegreesPerPixel = 0.15f;

    private float yaw;
    private float pitch;

    private void Awake()
    {
        var angles = transform.eulerAngles;
        yaw = angles.y;
        pitch = angles.x;
    }

    private void Update()
    {
        // IMGUI text fields retain a keyboard control while the user is editing them.
        // Do not also interpret A/D/W/S/Q/E as fly-camera input in that state.
        if (!Application.isPlaying || GUIUtility.keyboardControl != 0) return;
        var speed = movementMetersPerSecond * (Input.GetKey(KeyCode.LeftShift) ? 3f : 1f) * Time.deltaTime;
        var movement = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        if (Input.GetKey(KeyCode.E)) movement.y += 1f;
        if (Input.GetKey(KeyCode.Q)) movement.y -= 1f;
        transform.position += transform.TransformDirection(movement.normalized) * speed;

        if (!Input.GetMouseButton(1)) return;
        yaw += Input.GetAxis("Mouse X") * lookDegreesPerPixel * 100f;
        pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * lookDegreesPerPixel * 100f, -85f, 85f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }
}
