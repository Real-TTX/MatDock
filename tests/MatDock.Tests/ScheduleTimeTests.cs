using MatDock.Core.Backups;
using MatDock.Core.Schedules;
using Xunit;

namespace MatDock.Tests;

/// <summary>The times of schedules: in a zone (CRON_TZ=…, across summer time) or UTC as before, in words for the lists, and runs that can be cancelled.</summary>
public class ScheduleTimeTests
{
    [Fact]
    public void A_time_with_a_zone_is_of_that_zone_also_in_summer_time_and_one_without_is_UTC()
    {
        Assert.Equal(new DateTime(2026, 7, 15, 2, 0, 0, DateTimeKind.Utc), CronSchedule.GetNextUtc("0 2 * * *", new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(CronSchedule.IsValid("CRON_TZ=Mars/Olympus 0 2 * * *"));
        Assert.False(CronSchedule.IsValid("0 2 * *"));
        Assert.Equal(3, CronSchedule.Next("*/15 * * * *", DateTime.UtcNow, 3).Count);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Berlin", out _))
        {
            return; // no zone data (Windows with invariant globalization); the Linux run and the image have it
        }

        // 02:00 in Berlin: 01:00 UTC in winter, 00:00 UTC in summer.
        Assert.Equal(new DateTime(2026, 1, 15, 1, 0, 0, DateTimeKind.Utc), CronSchedule.GetNextUtc("CRON_TZ=Europe/Berlin 0 2 * * *", new DateTime(2026, 1, 14, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc), CronSchedule.GetNextUtc("CRON_TZ=Europe/Berlin 0 2 * * *", new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 15, 2, 0, 0, DateTimeKind.Utc), CronSchedule.GetNextUtc("0 2 * * *", new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)));

        Assert.True(CronSchedule.IsValid("CRON_TZ=Europe/Berlin 30 4 * * 1,4"));
        Assert.False(CronSchedule.IsValid("CRON_TZ=Mars/Olympus 0 2 * * *"));
        Assert.False(CronSchedule.IsValid("0 2 * *"));
        Assert.Equal(3, CronSchedule.Next("*/15 * * * *", DateTime.UtcNow, 3).Count);
    }

    [Theory]
    [InlineData("CRON_TZ=Europe/Berlin 0 2 * * *", "Daily at 02:00")]
    [InlineData("0 4 * * *", "Daily at 04:00 UTC")]
    [InlineData("CRON_TZ=Europe/Berlin 30 3 * * 1,4", "Mon, Thu at 03:30")]
    [InlineData("CRON_TZ=Europe/Berlin 0 6 * * 1-5", "Weekdays at 06:00")]
    [InlineData("CRON_TZ=Europe/Berlin 0 5 1 * *", "Monthly on the 1. at 05:00")]
    [InlineData("*/15 * * * *", "Every 15 min")]
    [InlineData("5 * * * *", "Hourly at :05")]
    [InlineData("0 */6 * * *", "Every 6 h")]
    [InlineData("0 4 1 1 *", "0 4 1 1 *")]
    public void A_time_is_said_in_words_where_it_can_be(string cron, string words) => Assert.Equal(words, CronText.Describe(cron));

    [Fact]
    public async Task A_run_is_known_while_it_runs_runs_once_and_can_be_cancelled()
    {
        var runs = new ScheduleRuns();
        using (var run = runs.Begin(7, "Nightly volumes", 1, CancellationToken.None)!)
        {
            Assert.NotNull(runs.Of(7));
            Assert.Null(runs.Begin(7, "Nightly volumes", 1, CancellationToken.None)); // not twice at once
            Assert.True(runs.Cancel(7));
            Assert.True(run.Cancelled);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(1000, run.Token));
        }

        Assert.Null(runs.Of(7));
        Assert.False(runs.Cancel(7));
    }
}
