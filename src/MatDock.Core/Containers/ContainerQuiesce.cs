using MatDock.Core.Execution;

namespace MatDock.Core.Containers;

/// <summary>Outcome of a stop phase: which containers were stopped and whether quiescing was complete.</summary>
public sealed record QuiesceResult(IReadOnlyList<string> StoppedIds, int RunningFound, bool ListFailed)
{
    public static readonly QuiesceResult None = new(Array.Empty<string>(), 0, false);

    /// <summary>True when not every running container using the volume could be stopped.</summary>
    public bool Incomplete => ListFailed || StoppedIds.Count < RunningFound;

    /// <summary>A user-facing warning when the snapshot may not be consistent, otherwise null.</summary>
    public string? Warning => !Incomplete
        ? null
        : ListFailed
            ? "Container konnten nicht ermittelt werden – Snapshot ggf. inkonsistent."
            : $"{RunningFound - StoppedIds.Count} von {RunningFound} Container konnten nicht gestoppt werden – Snapshot ggf. inkonsistent.";
}

/// <summary>
/// Stops the containers using a volume before a backup/migration/restore and restarts them afterwards,
/// so the archive is a consistent snapshot. Fully best-effort and exception-safe: no method here ever
/// throws, so a stop/restart failure can never abort or invalidate the surrounding operation. Only
/// running containers are stopped, and only the ones actually stopped are restarted.
/// </summary>
public static class ContainerQuiesce
{
    /// <summary>Stops the running containers mounting <paramref name="volume"/>. Never throws.</summary>
    public static QuiesceResult StopRunning(IHostSession client, string dockerHead, string volume, int timeoutSeconds)
    {
        var list = Run(client, ContainerCommands.ListByVolume(dockerHead, volume, runningOnly: true), timeoutSeconds);
        if (list.ExitStatus != 0)
        {
            return new QuiesceResult(Array.Empty<string>(), RunningFound: 0, ListFailed: true);
        }

        // The list is already running-only (no -a), so every parsed container is a stop candidate;
        // don't re-filter on State (absent on Docker < 20.10 -> would silently stop nothing).
        var running = ContainerService.ParseContainers(list.StdOut).Where(c => c.Id.Length > 0).ToList();

        var stopped = new List<string>();
        foreach (var c in running)
        {
            var result = Run(client, ContainerCommands.Action(dockerHead, ContainerAction.Stop, c.Id), timeoutSeconds);
            if (result.ExitStatus == 0)
            {
                stopped.Add(c.Id);
            }
        }

        return new QuiesceResult(stopped, running.Count, ListFailed: false);
    }

    /// <summary>Restarts the previously-stopped containers. Never throws.</summary>
    public static void Start(IHostSession client, string dockerHead, IEnumerable<string> ids, int timeoutSeconds)
    {
        foreach (var id in ids)
        {
            if (ContainerCommands.IsValidId(id))
            {
                Run(client, ContainerCommands.Action(dockerHead, ContainerAction.Start, id), timeoutSeconds);
            }
        }
    }

    // Best-effort: an SSH/timeout failure must never propagate out of a quiesce step (it runs in finally
    // blocks and around already-completed transfers), so treat any failure as a non-zero exit.
    private static (int ExitStatus, string StdOut, string StdErr) Run(IHostSession client, string command, int timeoutSeconds)
    {
        try
        {
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds));
            var stdout = cmd.Execute();
            return (cmd.ExitStatus ?? -1, stdout ?? string.Empty, cmd.Error ?? string.Empty);
        }
        catch
        {
            return (-1, string.Empty, string.Empty);
        }
    }
}
