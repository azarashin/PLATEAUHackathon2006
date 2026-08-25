using UnityEngine;

/// <summary>Runtime-visible provenance for a generated inspection Scene.</summary>
public sealed class EnvironmentCostInspectionMetadata : MonoBehaviour
{
    [SerializeField] private string areaId;
    [SerializeField] private int coordinateZoneId;
    [SerializeField] private Vector2 centerLongitudeLatitude;
    [SerializeField] private float radiusMeters;
    [SerializeField] private string analysisDate;
    [SerializeField] private string timezone;

    public string AreaId => areaId;
    public int CoordinateZoneId => coordinateZoneId;
    public double Longitude => centerLongitudeLatitude.x;
    public double Latitude => centerLongitudeLatitude.y;
    public float RadiusMeters => radiusMeters;
    public string AnalysisDate => analysisDate;
    public string Timezone => timezone;

    public void Configure(string newAreaId, int newCoordinateZoneId, double longitude, double latitude,
        double newRadiusMeters, string newAnalysisDate, string newTimezone)
    {
        areaId = newAreaId;
        coordinateZoneId = newCoordinateZoneId;
        centerLongitudeLatitude = new Vector2((float)longitude, (float)latitude);
        radiusMeters = (float)newRadiusMeters;
        analysisDate = newAnalysisDate;
        timezone = newTimezone;
    }
}
