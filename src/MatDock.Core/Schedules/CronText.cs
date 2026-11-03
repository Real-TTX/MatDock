using System.Globalization;
using MatDock.Core.Backups;

namespace MatDock.Core.Schedules;

/// <summary>A cron expression in words, for the lists: "Daily at 02:00", "Mon, Thu at 03:30", "Every 15 min". What it cannot put in words stays cron.</summary>
public static class CronText
{
    private static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static string Describe(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return "–";
        }

        var (expression, _, zoneId) = CronSchedule.Split(cron);
        var zone = zoneId is null ? " UTC" : string.Empty;
        var f = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5)
        {
            return cron;
        }

        var (min, hour, dom, month, dow) = (f[0], f[1], f[2], f[3], f[4]);
        if (month != "*")
        {
            return expression;
        }

        if (min.StartsWith("*/", StringComparison.Ordinal) && hour == "*" && dom == "*" && dow == "*")
        {
            return $"Every {min[2..]} min";
        }

        if (Number(min) is { } m && hour == "*" && dom == "*" && dow == "*")
        {
            return m == 0 ? "Hourly" : $"Hourly at :{m:00}";
        }

        if (Number(min) is { } mm && hour.StartsWith("*/", StringComparison.Ordinal) && dom == "*" && dow == "*")
        {
            return $"Every {hour[2..]} h{(mm == 0 ? string.Empty : $" at :{mm:00}")}";
        }

        var times = Times(hour, min);
        if (times is null)
        {
            return expression;
        }

        if (dom == "*" && dow == "*")
        {
            return $"Daily at {times}{zone}";
        }

        if (dom == "*" && WeekDays(dow) is { } days)
        {
            return $"{days} at {times}{zone}";
        }

        if (dow == "*" && Number(dom) is { } d)
        {
            return $"Monthly on the {d}. at {times}{zone}";
        }

        return expression;
    }

    private static int? Number(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    // "2" and "30" → "02:30"; "2,14" → "02:30, 14:30".
    private static string? Times(string hour, string min)
    {
        if (Number(min) is not { } m)
        {
            return null;
        }

        var hours = hour.Split(',').Select(Number).ToList();
        return hours.Any(h => h is null or > 23) ? null : string.Join(", ", hours.Select(h => $"{h:00}:{m:00}"));
    }

    private static string? WeekDays(string dow)
    {
        if (dow is "1-5" or "MON-FRI")
        {
            return "Weekdays";
        }

        if (dow is "0,6" or "6,0" or "SAT,SUN")
        {
            return "Weekends";
        }

        var days = dow.Split(',').Select(Number).ToList();
        return days.Any(d => d is null or > 7) ? null : string.Join(", ", days.Select(d => Days[d!.Value % 7]));
    }
}
