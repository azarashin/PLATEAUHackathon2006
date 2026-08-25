using UnityEngine;

/// <summary>Runtime-visible provenance for a generated inspection Scene.</summary>
public sealed class EnvironmentCostInspectionMetadata : MonoBehaviour
{
    [SerializeField] private string areaId;
    [SerializeField] private int coordinateZoneId;
    [SerializeField] private Vector2 centerLongitudeLatitude;
    [SerializeField] private float radiusMeters;

    public void Configure(string newAreaId, int newCoordinateZoneId, double longitude, double latitude, double newRadiusMeters)
    {
        areaId = newAreaId;
        coordinateZoneId = newCoordinateZoneId;
        centerLongitudeLatitude = new Vector2((float)longitude, (float)latitude);
        radiusMeters = (float)newRadiusMeters;
    }
}
