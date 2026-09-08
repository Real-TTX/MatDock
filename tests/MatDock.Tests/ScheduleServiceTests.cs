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
        public Task NotifyBackupResultAsync(string title, string summary, bool success, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(bool Ok, string Message)> SendTestAsync(CancellationToken ct = default) => Task.FromResult((true, "ok"));
        public Task<(bool Ok, string Message)> NotifyAsync(string subject, string body, CancellationToken ct = default)
        {
            NotifyCalls++;
            return Task.FromResult((true, "sent"));
        }
    }
}
