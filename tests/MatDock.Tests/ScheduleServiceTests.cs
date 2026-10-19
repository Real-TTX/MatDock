using MatDock.Core.Entities;
using MatDock.Core.Notifications;
using MatDock.Core.Schedules;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatDock.Tests;

public class ScheduleServiceTests
{
    private static ScheduleService Service(TestDatabase db, StubAction? action = null, StubNotifier? notifier = null)
        => new(db.Context,
            action is null ? Array.Empty<IScheduleAction>() : new IScheduleAction[] { action },
            notifier ?? new StubNotifier(),
            NullLogger<ScheduleService>.Instance);

    private static ScheduleInput CronInput(string cron = "0 4 * * *", ScheduleAction action = ScheduleAction.Summary) => new()
    {
        Name = "nightly",
        Enabled = true,
        Trigger = ScheduleTrigger.Cron,
        Cron = cron,
        Action = action,
    };

    [Fact]
    public async Task Create_cron_task_sets_next_run()
    {
        using var db = new TestDatabase();
        var (ok, _, id) = await Service(db).CreateAsync(CronInput());

        Assert.True(ok);
        var task = await Service(db).GetAsync(id);
        Assert.NotNull(task);
        Assert.NotNull(task!.NextRunAt);
        Assert.Equal(ScheduleTrigger.Cron, task.Trigger);
    }

    [Fact]
    public async Task Create_rejects_invalid_cron()
    {
        using var db = new TestDatabase();
        var (ok, message, _) = await Service(db).CreateAsync(CronInput(cron: "not a cron"));

        Assert.False(ok);
        Assert.Contains("cron", message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunNow_dispatches_to_matching_action_and_records_status()
    {
        using var db = new TestDatabase();
        var action = new StubAction(ScheduleAction.Summary, (true, "did the thing"));
        var svc = Service(db, action);
        var (_, _, id) = await svc.CreateAsync(CronInput());

        var summary = await svc.RunNowAsync(id);

        Assert.Equal("did the thing", summary);
        Assert.Equal(1, action.Calls);
        var task = await Service(db).GetAsync(id);
        Assert.Equal("did the thing", task!.LastStatus);
        Assert.NotNull(task.LastRunAt);
    }

    [Fact]
    public async Task RunNow_reports_when_no_handler_registered()
    {
        using var db = new TestDatabase();
        var svc = Service(db); // no actions registered
        var (_, _, id) = await svc.CreateAsync(CronInput(action: ScheduleAction.PruneVolumes));

        var summary = await svc.RunNowAsync(id);

        Assert.Contains("No handler", summary);
    }

    [Fact]
    public async Task NotifyOnResult_sends_a_notification()
    {
        using var db = new TestDatabase();
        var action = new StubAction(ScheduleAction.Summary, (true, "ok"));
        var notifier = new StubNotifier();
        var svc = Service(db, action, notifier);
        var input = CronInput();
        input.NotifyOnResult = true;
        var (_, _, id) = await svc.CreateAsync(input);

        await svc.RunNowAsync(id);

        Assert.Equal(1, notifier.NotifyCalls);
    }

    [Fact]
    public async Task DispatchEvent_runs_only_the_matching_event_task()
    {
        using var db = new TestDatabase();
        var action = new StubAction(ScheduleAction.Summary, (true, "ok"));
        var svc = Service(db, action);
        await svc.CreateAsync(new ScheduleInput
        {
            Name = "on-deploy-fail",
            Trigger = ScheduleTrigger.Event,
            Event = ScheduleEvent.DeployFailed,
            Action = ScheduleAction.Summary,
            Enabled = true,
        });

        Assert.Equal(1, await svc.DispatchEventAsync(ScheduleEvent.DeployFailed, 1, "stack"));
        Assert.Equal(0, await svc.DispatchEventAsync(ScheduleEvent.BackupFailed, 1, null));
        Assert.Equal(1, action.Calls);
    }

    [Fact]
    public async Task DispatchEvent_respects_environment_scope()
    {
        using var db = new TestDatabase();
        var action = new StubAction(ScheduleAction.Summary, (true, "ok"));
        var svc = Service(db, action);
        await svc.CreateAsync(new ScheduleInput
        {
            Name = "env-5-offline",
            Trigger = ScheduleTrigger.Event,
            Event = ScheduleEvent.EnvironmentOffline,
            Action = ScheduleAction.Summary,
            EnvironmentId = 5,
            Enabled = true,
        });

        Assert.Equal(0, await svc.DispatchEventAsync(ScheduleEvent.EnvironmentOffline, 9, null));
        Assert.Equal(1, await svc.DispatchEventAsync(ScheduleEvent.EnvironmentOffline, 5, null));
    }

    private static ScheduleInput EventInput(ScheduleEvent evt, ScheduleAction action) => new()
    {
        Name = "alert",
        Enabled = true,
        Trigger = ScheduleTrigger.Event,
        Event = evt,
        Action = action,
    };

    [Fact]
    public async Task A_notify_schedule_sends_the_event_itself()
    {
        using var db = new TestDatabase();
        var notifier = new StubNotifier();
        var svc = Service(db, notifier: notifier);
        await svc.CreateAsync(EventInput(ScheduleEvent.ContainerDied, ScheduleAction.Notify));

        Assert.Equal(1, await svc.DispatchEventAsync(ScheduleEvent.ContainerDied, 1, "Container db exited with code 1 on local."));

        Assert.Equal(1, notifier.NotifyCalls);
        Assert.Equal("MatDock: Container died", notifier.LastSubject);
        Assert.Equal("Container db exited with code 1 on local.", notifier.LastBody);
        var task = (await svc.GetAllAsync()).Single();
        Assert.True(task.LastRunOk);
        Assert.Equal("Container db exited with code 1 on local.", task.LastStatus);
    }

    [Fact]
    public async Task An_event_detail_goes_into_the_result_notification_of_other_actions()
    {
        using var db = new TestDatabase();
        var notifier = new StubNotifier();
        var svc = Service(db, new StubAction(ScheduleAction.HealthAlert, (true, "all fine")), notifier);
        var input = EventInput(ScheduleEvent.ContainerUnhealthy, ScheduleAction.HealthAlert);
        input.NotifyOnResult = true;
        await svc.CreateAsync(input);

        await svc.DispatchEventAsync(ScheduleEvent.ContainerUnhealthy, 1, "Container web is unhealthy on local.");

        Assert.Equal("Container web is unhealthy on local.\n\nall fine", notifier.LastBody);
    }

    [Fact]
    public async Task A_notification_needs_an_event_trigger()
    {
        using var db = new TestDatabase();
        var (ok, message, _) = await Service(db).CreateAsync(CronInput(action: ScheduleAction.Notify));
        Assert.False(ok);
        Assert.Contains("Event", message);
    }

    [Fact]
    public async Task Event_schedules_are_found_only_when_enabled_and_waiting_for_the_event()
    {
        using var db = new TestDatabase();
        var svc = Service(db);
        ScheduleEvent[] container = [ScheduleEvent.ContainerDied, ScheduleEvent.ContainerUnhealthy];
        Assert.False(await svc.HasEventSchedulesAsync(container));

        var off = EventInput(ScheduleEvent.ContainerDied, ScheduleAction.Notify);
        off.Enabled = false;
        await svc.CreateAsync(off);
        await svc.CreateAsync(EventInput(ScheduleEvent.DeployFailed, ScheduleAction.Notify));
        Assert.False(await svc.HasEventSchedulesAsync(container));

        await svc.CreateAsync(EventInput(ScheduleEvent.ContainerUnhealthy, ScheduleAction.Notify));
        Assert.True(await svc.HasEventSchedulesAsync(container));
    }

    [Fact]
    public async Task Update_options_are_kept_only_for_the_update_action()
    {
        using var db = new TestDatabase();
        var svc = Service(db);
        var input = CronInput(action: ScheduleAction.UpdateStacks);
        input.UpdateApply = true;
        input.UpdateBackupFirst = true;
        input.BundleStack = "media";
        var (ok, _, id) = await svc.CreateAsync(input);
        Assert.True(ok);
        var opt = ScheduleOptions.Parse((await svc.GetAsync(id))!.OptionsJson);
        Assert.True(opt.UpdateApply);
        Assert.True(opt.UpdateBackupFirst);
        Assert.Equal("media", opt.BundleStack);

        // Only checking: no backup option; another action: none of them.
        input.UpdateApply = false;
        await svc.UpdateAsync(id, input);
        Assert.False(ScheduleOptions.Parse((await svc.GetAsync(id))!.OptionsJson).UpdateBackupFirst);
        input.Action = ScheduleAction.PruneImages;
        input.UpdateApply = true;
        await svc.UpdateAsync(id, input);
        opt = ScheduleOptions.Parse((await svc.GetAsync(id))!.OptionsJson);
        Assert.False(opt.UpdateApply);
        Assert.Null(opt.BundleStack);

        input.Action = ScheduleAction.UpdateStacks;
        input.BundleStack = "Bad Name!";
        Assert.False((await svc.UpdateAsync(id, input)).Ok);
    }

    private sealed class StubAction : IScheduleAction
    {
        private readonly (bool, string) _result;
        public int Calls { get; private set; }
        public StubAction(ScheduleAction type, (bool, string) result) { Type = type; _result = result; }
        public ScheduleAction Type { get; }
        public Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class StubNotifier : INotificationService
    {
        public int NotifyCalls { get; private set; }
        public string? LastSubject { get; private set; }
        public string? LastBody { get; private set; }
        public Task NotifyBackupResultAsync(string title, string summary, bool success, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(bool Ok, string Message)> SendTestAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
        public Task<(bool Ok, string Message)> NotifyAsync(string subject, string body, CancellationToken ct = default)
        {
            NotifyCalls++;
            LastSubject = subject;
            LastBody = body;
            return Task.FromResult((true, "sent"));
        }
    }
}
