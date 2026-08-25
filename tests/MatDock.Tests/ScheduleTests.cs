using MatDock.Core.Backups;
using MatDock.Core.Entities;
using Xunit;

namespace MatDock.Tests;

public class ScheduleTests
{
    [Theory]
    [InlineData("0 3 * * *", true)]
    [InlineData("*/15 * * * *", true)]
    [InlineData("bogus", false)]
    [InlineData("", false)]
    [InlineData("60 3 * * *", false)] // minute out of range
    public void CronSchedule_validates(string cron, bool expected)
        => Assert.Equal(expected, CronSchedule.IsValid(cron));

    [Fact]
    public void CronSchedule_next_is_daily_0300_utc()
    {
        var from = new DateTime(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc);
        var next = CronSchedule.GetNextUtc("0 3 * * *", from);
        Assert.Equal(new DateTime(2026, 8, 26, 3, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Schedule_parses_and_dedupes_volume_names()
    {
        var s = new BackupSchedule { VolumesCsv = "app-data\n db \napp-data\n\n" };
        Assert.Equal(new[] { "app-data", "db" }, s.Volumes.ToArray());
    }
}
