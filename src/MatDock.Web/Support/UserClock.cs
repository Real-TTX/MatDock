namespace MatDock.Web.Support;

/// <summary>
/// The time zone of the person who looks at a page. A container usually runs in UTC, so "the server's local time" is not the time on the user's
/// wall: a check at 00:31 stood there as 23:31. The browser names its zone in a cookie (matdock.js), the request takes it up here, and every time a
/// page shows goes through <see cref="ToUserTime(DateTime)"/>. Without the cookie (the very first page, an API client) it is the server's zone as before.
/// </summary>
public static class UserClock
{
    /// <summary>The cookie in which the browser names its zone (an IANA id such as <c>Europe/Berlin</c>).</summary>
    public const string CookieName = "md-tz";

    private static readonly AsyncLocal<TimeZoneInfo?> Current = new();

    /// <summary>The zone of this request: the browser's, else the server's.</summary>
    public static TimeZoneInfo Zone => Current.Value ?? TimeZoneInfo.Local;

    /// <summary>The id of the zone the browser named (such as <c>Europe/Berlin</c>), or null when it named none: then a time is the server's.</summary>
    public static string? BrowserZoneId => Current.Value?.Id;

    /// <summary>Takes the zone the browser named for the rest of the request; an unknown id leaves the server's zone.</summary>
    public static void Use(string? zoneId) => Current.Value = Find(zoneId);

    internal static TimeZoneInfo? Find(string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId) || zoneId.Length > 64)
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.TryFindSystemTimeZoneById(Uri.UnescapeDataString(zoneId), out var zone) ? zone : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>A stored time (UTC, also when its kind is not said) as the user's clock shows it.</summary>
    public static DateTime ToUserTime(this DateTime time)
        => time.Kind == DateTimeKind.Local
            ? TimeZoneInfo.ConvertTime(time, Zone)
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(time, DateTimeKind.Utc), Zone);

    /// <summary>A point in time as the user's clock shows it.</summary>
    public static DateTimeOffset ToUserTime(this DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Zone);
}
