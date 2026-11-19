namespace Ari.Features.Analytics;

/// <summary>
/// Days and weeks in the site's calendar, not UTC. The zone comes from
/// ARI_ANALYTICS_TZ (an IANA id, UTC when unset or unknown), because Env is Platform's
/// and reads only its own variables.
/// </summary>
public sealed class AnalyticsClock
{
    public TimeZoneInfo Zone { get; }

    public AnalyticsClock()
    {
        var id = Environment.GetEnvironmentVariable("ARI_ANALYTICS_TZ");
        if (string.IsNullOrWhiteSpace(id)) id = "UTC";
        try { Zone = TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { Zone = TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { Zone = TimeZoneInfo.Utc; }
    }

    /// <summary>The site-local calendar day, e.g. "2026-11-19".</summary>
    public string Day(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).ToString("yyyy-MM-dd");

    /// <summary>Start of the site-local day (as a UTC instant) n days back, inclusive.</summary>
    public DateTimeOffset StartOfDay(DateTimeOffset utc, int daysBack)
    {
        var local = TimeZoneInfo.ConvertTime(utc, Zone).Date;
        var startLocal = local.AddDays(-daysBack);
        return TimeZoneInfo.ConvertTimeToUtc(startLocal, Zone);
    }

    /// <summary>The day list covering the last n days, oldest first.</summary>
    public List<string> Days(int days)
    {
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).Date;
        List<string> result = new(days);
        for (var i = days - 1; i >= 0; i--) result.Add(today.AddDays(-i).ToString("yyyy-MM-dd"));
        return result;
    }

    /// <summary>The ISO week ("2026-W46") containing the given instant, in site-local time.</summary>
    public string IsoWeek(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, Zone).Date;
        // ISO 8601: weeks start Monday, and the first week is the one with the year's first Thursday.
        var week = System.Globalization.ISOWeek.GetWeekOfYear(local);
        return $"{System.Globalization.ISOWeek.GetYear(local)}-W{week:00}";
    }

    /// <summary>The Monday that starts the ISO week containing the instant, site-local date.</summary>
    public DateTime MondayOf(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, Zone).Date;
        var offset = ((int)local.DayOfWeek + 6) % 7; // Monday -> 0
        return local.AddDays(-offset);
    }
}
