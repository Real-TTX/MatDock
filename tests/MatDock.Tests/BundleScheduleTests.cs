using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Notifications;
using MatDock.Core.Schedules;
using MatDock.Web.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatDock.Tests;

public class BundleScheduleTests
{
    // ---- frequency/time/weekday <-> cron ------------------------------------------------------

    [Theory]
    [InlineData("daily", "03:00", null, "0 3 * * *")]
    [InlineData("Daily", "23:59", "Monday", "59 23 * * *")]
    [InlineData("weekly", "03:30", "Sunday", "30 3 * * 0")]
    [InlineData("weekly", "4:05", "saturday", "5 4 * * 6")]
    [InlineData("weekly", "00:00", "wed", "0 0 * * 3")]
    [InlineData("weekly", "12:00", "7", "0 12 * * 0")]
    public void Builds_the_cron(string frequency, string time, string? weekday, string expected)
    {
        Assert.True(ScheduleTimes.TryBuildCron(frequency, time, weekday, out var cron, out var error), error);
        Assert.Equal(expected, cron);
        Assert.True(CronSchedule.IsValid(cron));
    }

    [Theory]
    [InlineData("hourly", "03:00", null, "frequency")]
    [InlineData("daily", "24:00", null, "time")]
    [InlineData("daily", "3", null, "time")]
    [InlineData("daily", "03:7", null, "time")]
    [InlineData("weekly", "03:00", "Funday", "weekday")]
    [InlineData("weekly", "03:00", null, "weekday")]
    public void Rejects_invalid_schedules(string frequency, string time, string? weekday, string field)
    {
        Assert.False(ScheduleTimes.TryBuildCron(frequency, time, weekday, out _, out var error));
        Assert.Contains(field, error);
    }

    [Theory]
    [InlineData("0 3 * * *", "daily", "03:00", null)]
    [InlineData("30 3 * * 0", "weekly", "03:30", "Sunday")]
    [InlineData("5 22 * * 5", "weekly", "22:05", "Friday")]
    [InlineData("0 */6 * * *", "custom", null, null)]
    [InlineData("0 3 1 * *", "custom", null, null)]
    [InlineData(null, "custom", null, null)]
    public void Describes_a_cron(string? cron, string frequency, string? time, string? weekday)
        => Assert.Equal((frequency, time, weekday), ScheduleTimes.Describe(cron));

    // ---- API body -> ScheduleInput ------------------------------------------------------------

    [Fact]
    public void Empty_body_takes_the_contract_defaults()
    {
        var (input, error) = BackupsApi.BuildScheduleInput("mattiverse-nightly", null);

        Assert.Null(error);
        Assert.Equal(ScheduleAction.BundleBackup, input!.Action);
        Assert.Equal("0 3 * * *", input.Cron);
        Assert.Equal(7, input.RetentionCount);
        Assert.True(input.StopContainers);
        Assert.True(input.Enabled);
        Assert.Null(input.BundleStack);
    }

    [Fact]
    public void Body_maps_to_the_schedule()
    {
        var (input, error) = BackupsApi.BuildScheduleInput(" weekly-app ", new BackupScheduleBody
        {
            Stack = "mattiverse-app",
            ManagedBy = "mattiverse",
            Frequency = "weekly",
            Time = "02:15",
            Weekday = "Tuesday",
            Retention = 3,
            StopContainers = false,
            Enabled = false,
            TargetId = 4,
        });

        Assert.Null(error);
        Assert.Equal("weekly-app", input!.Name);
        Assert.Equal("15 2 * * 2", input.Cron);
        Assert.Equal("mattiverse-app", input.BundleStack);
        Assert.Equal("mattiverse", input.ManagedBy);
        Assert.Equal(3, input.RetentionCount);
        Assert.False(input.StopContainers);
        Assert.False(input.Enabled);
        Assert.Equal(4, input.BackupTargetId);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("ok", "Bad Stack", null)]
    [InlineData("ok", null, -1)]
    [InlineData("ok", null, 1001)]
    public void Invalid_bodies_are_rejected(string name, string? stack, int? retention)
    {
        var (input, error) = BackupsApi.BuildScheduleInput(name, new BackupScheduleBody { Stack = stack, Retention = retention });
        Assert.Null(input);
        Assert.NotNull(error);
    }

    // ---- stored as a ScheduledTask ------------------------------------------------------------

    private static ScheduleService Schedules(TestDatabase db)
        => new(db.Context, Array.Empty<IScheduleAction>(), new SilentNotifier(), NullLogger<ScheduleService>.Instance);

    [Fact]
    public async Task Bundle_schedule_round_trips_and_ui_saves_keep_the_owner()
    {
        using var db = new TestDatabase();
        var schedules = Schedules(db);
        var (input, _) = BackupsApi.BuildScheduleInput("mattiverse-nightly", new BackupScheduleBody { ManagedBy = "mattiverse", Retention = 5 });
        input!.EnvironmentId = 1;

        var (ok, message, id) = await schedules.CreateAsync(input);
        Assert.True(ok, message);

        var found = await schedules.FindBundleScheduleAsync("mattiverse-nightly");
        Assert.Equal(id, found!.Id);
        Assert.Equal("mattiverse", found.ManagedBy);
        Assert.NotNull(found.NextRunAt);
        var options = ScheduleOptions.Parse(found.OptionsJson);
        Assert.Equal(5, options.RetentionCount);
        Assert.True(options.StopContainers);

        // The Schedules page saves without ManagedBy: the owner stays.
        await schedules.UpdateAsync(id, new ScheduleInput
        {
            Name = "mattiverse-nightly", Trigger = ScheduleTrigger.Cron, Cron = "0 5 * * *", Action = ScheduleAction.BundleBackup,
            EnvironmentId = 1, RetentionCount = 2, BundleStack = "app",
        });
        var updated = await schedules.GetAsync(id);
        Assert.Equal("mattiverse", updated!.ManagedBy);
        Assert.Equal("app", ScheduleOptions.Parse(updated.OptionsJson).BundleStack);

        var dto = System.Text.Json.JsonSerializer.Serialize(BackupsApi.ToScheduleDto(updated), ApiResults.Json);
        Assert.Contains("\"frequency\":\"daily\"", dto);
        Assert.Contains("\"time\":\"05:00\"", dto);
        Assert.Contains("\"retention\":2", dto);
        Assert.Contains("\"stack\":\"app\"", dto);
        Assert.Contains("\"managedBy\":\"mattiverse\"", dto);
    }

    [Fact]
    public async Task Only_bundle_schedules_are_found_by_name()
    {
        using var db = new TestDatabase();
        var schedules = Schedules(db);
        await schedules.CreateAsync(new ScheduleInput { Name = "nightly", Cron = "0 4 * * *", Action = ScheduleAction.PruneImages });

        Assert.Null(await schedules.FindBundleScheduleAsync("nightly"));
        Assert.Empty(await schedules.GetBundleSchedulesAsync());
    }

    [Fact]
    public async Task Bundle_schedule_rejects_an_invalid_stack()
    {
        using var db = new TestDatabase();
        var (ok, _, _) = await Schedules(db).CreateAsync(new ScheduleInput
        {
            Name = "x", Cron = "0 4 * * *", Action = ScheduleAction.BundleBackup, BundleStack = "Not valid",
        });
        Assert.False(ok);
    }

    // ---- which stacks, which bundles to keep -------------------------------------------------

    [Fact]
    public void Schedule_without_stack_covers_the_stacks_of_its_owner()
    {
        var stacks = new[]
        {
            new Stack { Name = "mattiverse-b", EnvironmentId = 1, ManagedBy = "mattiverse" },
            new Stack { Name = "mattiverse-a", EnvironmentId = 1, ManagedBy = "Mattiverse" },
            new Stack { Name = "own", EnvironmentId = 1 },
            new Stack { Name = "mattiverse-c", EnvironmentId = 2, ManagedBy = "mattiverse" },
        };

        Assert.Equal(["mattiverse-a", "mattiverse-b"], BundleBackupAction.SelectStacks(stacks, 1, "mattiverse"));
        Assert.Equal(["mattiverse-a", "mattiverse-b", "own"], BundleBackupAction.SelectStacks(stacks, 1, null));
    }

    private static StackBackup B(long id, int hoursAgo, StackBackupStatus status = StackBackupStatus.Succeeded)
        => new() { Id = id, CreateDate = Now.AddHours(-hoursAgo), Status = status, Kind = StackBackupKind.Scheduled };

    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Retention_keeps_the_newest_successful_bundles()
    {
        var bundles = new[] { B(1, 72), B(2, 48), B(3, 24), B(4, 1) };
        Assert.Equal([2, 1], BundleBackupService.SelectForRetention(bundles, keepCount: 2, keepDays: 0, Now));
        Assert.Empty(BundleBackupService.SelectForRetention(bundles, keepCount: 0, keepDays: 0, Now));
    }

    [Fact]
    public void Retention_drops_superseded_failures_and_old_bundles()
    {
        var bundles = new[] { B(1, 30), B(2, 20, StackBackupStatus.Failed), B(3, 10), B(4, 2, StackBackupStatus.Failed) };

        // The failure after the newest success (4) stays visible; the older one (2) is superseded.
        Assert.Equal([2], BundleBackupService.SelectForRetention(bundles, keepCount: 5, keepDays: 0, Now));
        // Older than one day: 1 goes as well.
        Assert.Equal([2, 1], BundleBackupService.SelectForRetention(bundles, keepCount: 5, keepDays: 1, Now));
    }

    private sealed class SilentNotifier : INotificationService
    {
        public Task NotifyBackupResultAsync(string title, string summary, bool success, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(bool Ok, string Message)> SendTestAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
        public Task<(bool Ok, string Message)> NotifyAsync(string subject, string body, CancellationToken ct = default) => Task.FromResult((true, "ok"));
    }
}
