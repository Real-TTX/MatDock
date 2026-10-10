using System.Globalization;

namespace MatDock.Core.Schedules;

/// <summary>
/// Translates the simple schedule of the API (<c>frequency</c> daily/weekly, <c>time</c> "HH:mm", <c>weekday</c>) to the
/// 5-field cron that MatDock's schedules run on (UTC), and back. A cron that was edited to something else in the
/// Schedules page reads back as frequency <c>custom</c>.
/// </summary>
public static class ScheduleTimes
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Custom = "custom";

    /// <summary>Cron weekday numbers: Sunday = 0 … Saturday = 6.</summary>
    public static readonly string[] Weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>Builds the cron for a daily or weekly schedule; false with a readable error for invalid input.</summary>
    public static bool TryBuildCron(string? frequency, string? time, string? weekday, out string cron, out string error)
    {
        cron = string.Empty;
        error = string.Empty;

        if (!TryParseTime(time, out var hour, out var minute))
        {
            error = "time must be HH:mm (00:00-23:59).";
            return false;
        }

        switch ((frequency ?? string.Empty).Trim().ToLowerInvariant())
        {
            case Daily:
                cron = $"{minute} {hour} * * *";
                return true;

            case Weekly:
                if (!TryParseWeekday(weekday, out var day))
                {
                    error = "weekday must be one of " + string.Join(", ", Weekdays) + ".";
                    return false;
                }

                cron = $"{minute} {hour} * * {day}";
                return true;

            default:
                error = "frequency must be 'daily' or 'weekly'.";
                return false;
        }
    }

    /// <summary>Reads a cron back as frequency/time/weekday; anything but "m h * * *" or "m h * * d" is <c>custom</c>
    /// (time and weekday null).</summary>
    public static (string Frequency, string? Time, string? Weekday) Describe(string? cron)
    {
        var parts = (cron ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minute) && minute is >= 0 and <= 59
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var hour) && hour is >= 0 and <= 23
            && parts[2] == "*" && parts[3] == "*")
        {
            var time = $"{hour:00}:{minute:00}";
            if (parts[4] == "*")
            {
                return (Daily, time, null);
            }

            if (int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 0 and <= 7)
            {
                return (Weekly, time, Weekdays[day % 7]);
            }
        }

        return (Custom, null, null);
    }

    private static bool TryParseTime(string? time, out int hour, out int minute)
    {
        hour = minute = 0;
        var parts = (time ?? string.Empty).Trim().Split(':');
        return parts.Length == 2
               && parts[0].Length is 1 or 2 && parts[1].Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour) && hour is >= 0 and <= 23
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute) && minute is >= 0 and <= 59;
    }

    private static bool TryParseWeekday(string? weekday, out int day)
    {
        day = Array.FindIndex(Weekdays, w => string.Equals(w, weekday?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (day >= 0)
        {
            return true;
        }

        // Also accept the three-letter abbreviation ("sun") and the cron number (0-6, 7 = Sunday).
        var text = (weekday ?? string.Empty).Trim();
        day = Array.FindIndex(Weekdays, w => text.Length == 3 && w.StartsWith(text, StringComparison.OrdinalIgnoreCase));
        if (day >= 0)
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 7)
        {
            day = number % 7;
            return true;
        }

        day = -1;
        return false;
    }
}
