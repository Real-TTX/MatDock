using MatDock.Core.Entities;
using MatDock.Core.Schedules;

namespace MatDock.Web.Support;

/// <summary>How the pages show a schedule: what it does in words, its state (running, ok, failed), and the start of a run that does not hold the page.</summary>
public static class ScheduleView
{
    /// <summary>What a schedule works on: the volumes or the stack, and the target of a backup.</summary>
    public static string Scope(ScheduledTask task, IReadOnlyDictionary<long, string> targetNames)
    {
        var o = ScheduleOptions.Parse(task.OptionsJson);
        string Target() => o.BackupTargetId is { } id && targetNames.TryGetValue(id, out var name) ? name : "Local";
        return task.Action switch
        {
            ScheduleAction.Backup => $"{(o.All ? "All volumes" : Short(o.VolumesCsv))} → {Target()}{(o.StopContainers ? " · stopped" : string.Empty)}",
            ScheduleAction.BundleBackup => $"{(string.IsNullOrWhiteSpace(o.BundleStack) ? "All stacks" : "Stack " + o.BundleStack)} → {Target()}{(o.StopContainers ? " · stopped" : string.Empty)}",
            ScheduleAction.SelfBackup => $"MatDock → {Target()}",
            ScheduleAction.UpdateStacks => o.UpdateApply ? "Apply updates" : "Check for updates",
            _ => string.Empty,
        };
    }

    private static string Short(string? csv)
    {
        var names = (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length switch
        {
            0 => "no volumes",
            <= 2 => string.Join(", ", names),
            _ => $"{names[0]}, {names[1]} +{names.Length - 2}",
        };
    }

    /// <summary>The state of a schedule for a pill: running (since when), ok, failed, or never run.</summary>
    public static (string Tone, string Label, string? Title) State(ScheduledTask task, ScheduleRuns.Run? run)
    {
        if (run is not null)
        {
            return ("mat-pill--busy", run.Cancelling ? "cancelling…" : $"running {Since(run.StartedUtc)}", $"Started {run.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        }

        if (task.LastRunAt is null)
        {
            return ("mat-pill--plain", "never run", null);
        }

        var ok = task.LastRunOk ?? !(task.LastStatus ?? string.Empty).StartsWith("Error", StringComparison.OrdinalIgnoreCase);
        var cancelled = (task.LastStatus ?? string.Empty).StartsWith("Cancelled", StringComparison.Ordinal);
        return cancelled ? ("mat-pill--warn", "cancelled", task.LastStatus)
            : ok ? ("mat-pill--ok", "ok", task.LastStatus)
            : ("mat-pill--error", "failed", task.LastStatus);
    }

    /// <summary>"5 s", "3 min", "2 h": how long something runs.</summary>
    public static string Since(DateTime startedUtc)
    {
        var d = DateTime.UtcNow - startedUtc;
        return d.TotalSeconds < 60 ? $"{Math.Max(0, (int)d.TotalSeconds)} s" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalHours} h {d.Minutes} min";
    }

    /// <summary>"in 5 min", "3 h ago": a time relative to now.</summary>
    public static string Relative(DateTime? utc)
    {
        if (utc is not { } t)
        {
            return "–";
        }

        var d = t - DateTime.UtcNow;
        var past = d < TimeSpan.Zero;
        var a = past ? -d : d;
        var text = a.TotalMinutes < 1 ? "1 min" : a.TotalMinutes < 60 ? $"{(int)a.TotalMinutes} min" : a.TotalHours < 24 ? $"{(int)a.TotalHours} h" : $"{(int)a.TotalDays} d";
        return past ? (a.TotalMinutes < 1 ? "just now" : text + " ago") : "in " + text;
    }

    /// <summary>
    /// Starts a run of a schedule in the background (a backup takes minutes; the page answers at once and shows it running). False when it runs already.
    /// </summary>
    public static bool StartNow(IServiceScopeFactory scopes, ScheduleRuns runs, long id, CancellationToken stopping, ILogger logger)
    {
        if (runs.Of(id) is not null)
        {
            return false;
        }

        // Without the request's context: the run is the system's (as from the schedule runner), not of the request that ends now.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ScheduleService>().RunNowAsync(id, stopping);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "The run of schedule {Id} failed.", id);
                }
            }, CancellationToken.None);
        }

        // The page that follows shows it running: wait (briefly) until the run has begun.
        for (var i = 0; i < 20 && runs.Of(id) is null; i++)
        {
            Thread.Sleep(50);
        }

        return true;
    }
}
