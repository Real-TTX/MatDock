using MatDock.Core.Backups;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Schedules;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Stacks;

/// <summary>
/// The automatic sync of a Git stack (<see cref="Stack.GitSyncCron"/>): a scheduled task of the stack's own (<c>SourceKind</c> <see cref="Source"/>,
/// <c>SourceId</c> = the stack) runs <see cref="StackService.SyncGitAsync"/>. A task whose stack is gone or no longer syncs switches itself off.
/// </summary>
public sealed class GitSyncAction : IScheduleAction
{
    /// <summary>The <c>SourceKind</c> of the tasks of the Git syncs.</summary>
    public const string Source = "gitsync";

    private readonly MatDockDbContext _db;
    private readonly StackService _stacks;

    public GitSyncAction(MatDockDbContext db, StackService stacks)
    {
        _db = db;
        _stacks = stacks;
    }

    public ScheduleAction Type => ScheduleAction.GitSync;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var stack = task.SourceId is { } id ? await _db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) : null;
        if (stack is null || !stack.IsGitBacked || string.IsNullOrWhiteSpace(stack.GitSyncCron))
        {
            var tracked = await _db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == task.Id, ct);
            if (tracked is not null)
            {
                tracked.Enabled = false;
                tracked.NextRunAt = null;
                await _db.SaveChangesAsync(ct);
            }

            return (false, "The stack is gone or no longer syncs from Git: the task is switched off.");
        }

        var result = await _stacks.SyncGitAsync(stack.Id, new DeployRequest(Trigger: DeployTrigger.Sync, Actor: "Schedule"), ct: ct);
        return (result.Outcome != GitSyncOutcome.Failed, $"{stack.Name}: {Describe(result)}");
    }

    /// <summary>One line about a sync.</summary>
    public static string Describe(GitSyncResult result) => result.Outcome switch
    {
        GitSyncOutcome.Deployed => $"deployed commit {Short(result.Commit)}.",
        GitSyncOutcome.Skipped => result.Message,
        _ => "failed: " + LastLine(result.Message),
    };

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "?" : sha[..Math.Min(8, sha.Length)];

    private static string LastLine(string text)
        => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "unknown error";
}

/// <summary>Keeps the scheduled task of a Git stack's sync in line with <see cref="Stack.GitSyncCron"/> (created, changed, removed).</summary>
public sealed class GitSyncSchedules
{
    private readonly ScheduleService _schedules;

    public GitSyncSchedules(ScheduleService schedules) => _schedules = schedules;

    public Task ApplyAsync(Stack stack, CancellationToken ct = default)
        => stack.IsGitBacked && CronSchedule.IsValid(stack.GitSyncCron)
            ? _schedules.UpsertSourceTaskAsync(GitSyncAction.Source, stack.Id, $"Git sync · {stack.Name}", stack.EnvironmentId, stack.GitSyncCron!.Trim(),
                enabled: true, ScheduleAction.GitSync, new ScheduleOptions(), ct)
            : _schedules.RemoveSourceTaskAsync(GitSyncAction.Source, stack.Id, ct);

    public Task RemoveAsync(long stackId, CancellationToken ct = default) => _schedules.RemoveSourceTaskAsync(GitSyncAction.Source, stackId, ct);
}
