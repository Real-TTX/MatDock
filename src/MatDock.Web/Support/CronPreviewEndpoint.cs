using System.Globalization;
using MatDock.Core.Backups;
using MatDock.Core.Schedules;

namespace MatDock.Web.Support;

/// <summary><c>GET /ui/cron?expr=</c>: a cron line in words and its next runs in its zone, for the time editor (_CronEditor).</summary>
public static class CronPreviewEndpoint
{
    public const string Path = "/ui/cron";

    public static IResult Handle(string? expr)
    {
        if (!CronSchedule.IsValid(expr))
        {
            var unknownZone = expr is not null && CronSchedule.Split(expr).Zone is null;
            return Results.Json(new { valid = false, error = unknownZone ? "the time zone is not known here." : "check the five fields (minute hour day month weekday)." });
        }

        var zone = CronSchedule.Split(expr!).Zone ?? TimeZoneInfo.Utc;
        var next = CronSchedule.Next(expr!, DateTime.UtcNow, 3)
            .Select(t => TimeZoneInfo.ConvertTimeFromUtc(t, zone).ToString("ddd dd.MM. HH:mm", CultureInfo.InvariantCulture))
            .ToList();
        return Results.Json(new { valid = true, text = CronText.Describe(expr), next });
    }
}
