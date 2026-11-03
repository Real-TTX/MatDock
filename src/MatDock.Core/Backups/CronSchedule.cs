using Cronos;

namespace MatDock.Core.Backups;

/// <summary>
/// Thin wrapper around Cronos for validating and evaluating 5-field cron expressions. Without a zone they are UTC (as they always were); with a leading
/// <c>CRON_TZ=Europe/Berlin</c> (as in cronie) the times are of that zone, also across summer time.
/// </summary>
public static class CronSchedule
{
    public const string ZonePrefix = "CRON_TZ=";

    /// <summary>The expression and its zone (UTC without one); the zone is null when it is unknown.</summary>
    public static (string Expression, TimeZoneInfo? Zone, string? ZoneId) Split(string cron)
    {
        var text = cron.Trim();
        if (!text.StartsWith(ZonePrefix, StringComparison.Ordinal))
        {
            return (text, TimeZoneInfo.Utc, null);
        }

        var space = text.IndexOf(' ');
        var id = space < 0 ? text[ZonePrefix.Length..] : text[ZonePrefix.Length..space];
        var expression = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        return (expression, TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null, id);
    }

    public static bool IsValid(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return false;
        }

        var (expression, zone, _) = Split(cron);
        if (zone is null)
        {
            return false;
        }

        try
        {
            CronExpression.Parse(expression);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    /// <summary>Next occurrence strictly after <paramref name="fromUtc"/>, or null if none.</summary>
    public static DateTime? GetNextUtc(string cron, DateTime fromUtc)
    {
        var (text, zone, _) = Split(cron);
        var expression = CronExpression.Parse(text);
        var from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        return expression.GetNextOccurrence(from, zone ?? TimeZoneInfo.Utc);
    }

    /// <summary>The next <paramref name="count"/> occurrences after <paramref name="fromUtc"/> (for the editor's preview).</summary>
    public static IReadOnlyList<DateTime> Next(string cron, DateTime fromUtc, int count)
    {
        var list = new List<DateTime>();
        var at = fromUtc;
        while (list.Count < count && GetNextUtc(cron, at) is { } next)
        {
            list.Add(next);
            at = next;
        }

        return list;
    }
}
