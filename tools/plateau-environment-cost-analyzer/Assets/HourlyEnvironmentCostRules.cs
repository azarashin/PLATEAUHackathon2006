using System;
using System.Globalization;
using UnityEngine;

public static class HourlyEnvironmentCostRules
{
    public const double FormulaToleranceSeconds = 1e-6;

    public static double CalculateSolarExposureSeconds(double walkingSeconds, double shadeRatio)
    {
        if (walkingSeconds <= 0.0) throw new ArgumentOutOfRangeException(nameof(walkingSeconds));
        if (shadeRatio < 0.0 || shadeRatio > 1.0) throw new ArgumentOutOfRangeException(nameof(shadeRatio));
        return walkingSeconds * (1.0 - shadeRatio);
    }

    public static string DetermineStatus(int sampleCount, int validSampleCount, int noGroundSampleCount,
        double sunElevationDegrees, out string exclusionReason)
    {
        if (sampleCount < 0 || validSampleCount < 0 || noGroundSampleCount < 0 ||
            validSampleCount + noGroundSampleCount != sampleCount)
        {
            throw new ArgumentException("sample coverage counts are inconsistent.");
        }
        if (sunElevationDegrees <= 0.0)
        {
            exclusionReason = "sun-below-horizon";
            return "missing";
        }
        if (validSampleCount == 0)
        {
            exclusionReason = "road-surface-not-found";
            return "missing";
        }
        if (noGroundSampleCount > 0)
        {
            exclusionReason = "some-road-samples-not-found";
            return "partial";
        }
        exclusionReason = null;
        return "available";
    }

    public static string Timestamp(DateTime analysisDate, int localHour, string timezone)
    {
        if (localHour < 0 || localHour > 23) throw new ArgumentOutOfRangeException(nameof(localHour));
        var local = DateTime.SpecifyKind(analysisDate.Date.AddHours(localHour), DateTimeKind.Unspecified);
        var zone = ResolveTimeZone(timezone);
        var offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Calculates the local solar position used by both the batch analyser and the Scene-view inspector.
    /// Keeping this calculation here prevents the visual inspection from using a different sun direction
    /// from the analysis output that it is explaining.
    /// </summary>
    public static SunPosition CalculateSun(DateTime analysisDate, int localHour, double latitudeDegrees,
        double longitudeDegrees, string timezone)
    {
        if (localHour < 0 || localHour > 23) throw new ArgumentOutOfRangeException(nameof(localHour));
        return CalculateSun(analysisDate, (double)localHour, latitudeDegrees, longitudeDegrees, timezone);
    }

    /// <summary>
    /// Calculates a solar position for a local civil time. The hour may contain a fractional part so the
    /// runtime time slider can move shadows continuously, while batch analysis continues to use whole hours.
    /// Azimuth is degrees clockwise from true north and direction points from the ground towards the sun.
    /// </summary>
    public static SunPosition CalculateSun(DateTime analysisDate, double localHour, double latitudeDegrees,
        double longitudeDegrees, string timezone)
    {
        if (localHour < 0.0 || localHour >= 24.0) throw new ArgumentOutOfRangeException(nameof(localHour));
        if (latitudeDegrees < -90.0 || latitudeDegrees > 90.0) throw new ArgumentOutOfRangeException(nameof(latitudeDegrees));
        if (longitudeDegrees < -180.0 || longitudeDegrees > 180.0) throw new ArgumentOutOfRangeException(nameof(longitudeDegrees));

        var local = DateTime.SpecifyKind(analysisDate.Date.AddHours(localHour), DateTimeKind.Unspecified);
        var timezoneHours = ResolveTimeZone(timezone).GetUtcOffset(local).TotalHours;
        var dayOfYear = analysisDate.DayOfYear;
        var fractionalYear = 2.0 * Math.PI / 365.0 * (dayOfYear - 1 + (localHour - 12.0) / 24.0);
        var equationOfTime = 229.18 * (0.000075 + 0.001868 * Math.Cos(fractionalYear)
            - 0.032077 * Math.Sin(fractionalYear) - 0.014615 * Math.Cos(2 * fractionalYear)
            - 0.040849 * Math.Sin(2 * fractionalYear));
        var declination = 0.006918 - 0.399912 * Math.Cos(fractionalYear)
            + 0.070257 * Math.Sin(fractionalYear) - 0.006758 * Math.Cos(2 * fractionalYear)
            + 0.000907 * Math.Sin(2 * fractionalYear) - 0.002697 * Math.Cos(3 * fractionalYear)
            + 0.00148 * Math.Sin(3 * fractionalYear);
        var timeOffset = equationOfTime + 4.0 * longitudeDegrees - 60.0 * timezoneHours;
        var trueSolarMinutes = localHour * 60.0 + timeOffset;
        var hourAngle = (trueSolarMinutes / 4.0 - 180.0) * Math.PI / 180.0;
        var latitude = latitudeDegrees * Math.PI / 180.0;
        var cosineZenith = Math.Sin(latitude) * Math.Sin(declination)
            + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngle);
        cosineZenith = Math.Max(-1.0, Math.Min(1.0, cosineZenith));
        var zenith = Math.Acos(cosineZenith);
        var elevationDegrees = 90.0 - zenith * 180.0 / Math.PI;
        var azimuthDegrees = (Math.Atan2(Math.Sin(hourAngle),
            Math.Cos(hourAngle) * Math.Sin(latitude) - Math.Tan(declination) * Math.Cos(latitude))
            * 180.0 / Math.PI + 180.0) % 360.0;
        var elevation = elevationDegrees * Math.PI / 180.0;
        var azimuth = azimuthDegrees * Math.PI / 180.0;
        var direction = new Vector3((float)(Math.Sin(azimuth) * Math.Cos(elevation)),
            (float)Math.Sin(elevation), (float)(Math.Cos(azimuth) * Math.Cos(elevation))).normalized;
        return new SunPosition(elevationDegrees, azimuthDegrees, direction);
    }

    private static TimeZoneInfo ResolveTimeZone(string timezone)
    {
        if (string.Equals(timezone, "Asia/Tokyo", StringComparison.Ordinal))
        {
            foreach (var candidate in new[] { "Asia/Tokyo", "Tokyo Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
        }
        try { return TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException || exception is InvalidTimeZoneException)
        {
            throw new ArgumentException($"Unsupported timezone: {timezone}", nameof(timezone), exception);
        }
    }

    public readonly struct SunPosition
    {
        public readonly double elevationDegrees;
        public readonly double azimuthDegrees;
        public readonly Vector3 direction;

        public SunPosition(double elevationDegrees, double azimuthDegrees, Vector3 direction)
        {
            this.elevationDegrees = elevationDegrees;
            this.azimuthDegrees = azimuthDegrees;
            this.direction = direction;
        }
    }
}
