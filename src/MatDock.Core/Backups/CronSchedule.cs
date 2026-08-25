using Cronos;

namespace MatDock.Core.Backups;

/// <summary>Thin wrapper around Cronos for validating and evaluating 5-field cron expressions (UTC).</summary>
public static class CronSchedule
{
    public static bool IsValid(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return false;
        }

        try
        {
            CronExpression.Parse(cron.Trim());
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
        var expression = CronExpression.Parse(cron.Trim());
        var from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        return expression.GetNextOccurrence(from, TimeZoneInfo.Utc);
    }
}
