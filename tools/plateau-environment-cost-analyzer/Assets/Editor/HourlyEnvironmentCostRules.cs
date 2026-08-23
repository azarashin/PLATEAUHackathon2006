using System;
using System.Globalization;

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
}
