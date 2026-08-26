using Renci.SshNet;

namespace MatDock.Core.Containers;

/// <summary>
/// Stops the containers using a volume before a backup/migration/restore and restarts them afterwards,
/// so the archive is a consistent snapshot. Best-effort: only running containers are stopped, and only
/// the ones actually stopped are restarted (containers already stopped are left as they were).
/// </summary>
public static class ContainerQuiesce
{
    /// <summary>Stops the running containers mounting <paramref name="volume"/>; returns the ids to restart later.</summary>
    public static IReadOnlyList<string> StopRunning(SshClient client, string dockerHead, string volume, int timeoutSeconds)
    {
        var list = Run(client, ContainerCommands.ListByVolume(dockerHead, volume, runningOnly: true), timeoutSeconds);
        if (list.ExitStatus != 0)
        {
            return Array.Empty<string>();
        }

        var stopped = new List<string>();
        foreach (var container in ContainerService.ParseContainers(list.StdOut))
        {
            if (!container.IsRunning || string.IsNullOrEmpty(container.Id))
            {
                continue;
            }

            var result = Run(client, ContainerCommands.Action(dockerHead, ContainerAction.Stop, container.Id), timeoutSeconds);
            if (result.ExitStatus == 0)
            {
                stopped.Add(container.Id);
            }
        }

        return stopped;
    }

    /// <summary>Restarts the previously-stopped containers (best-effort).</summary>
    public static void Start(SshClient client, string dockerHead, IEnumerable<string> ids, int timeoutSeconds)
    {
        foreach (var id in ids)
        {
            if (ContainerCommands.IsValidId(id))
            {
                Run(client, ContainerCommands.Action(dockerHead, ContainerAction.Start, id), timeoutSeconds);
            }
        }
    }

    private static (int ExitStatus, string StdOut, string StdErr) Run(SshClient client, string command, int timeoutSeconds)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds));
        var stdout = cmd.Execute();
        return (cmd.ExitStatus ?? -1, stdout ?? string.Empty, cmd.Error ?? string.Empty);
    }
}
