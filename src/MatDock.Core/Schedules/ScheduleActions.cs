using System.Text;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Notifications;

namespace MatDock.Core.Schedules;

/// <summary>Shared helper: resolves the environments a task targets (one, or all enabled).</summary>
public abstract class ScheduleActionBase
{
    protected readonly EnvironmentService Environments;
    protected readonly IEnvironmentConnectionService Connection;

    protected ScheduleActionBase(EnvironmentService environments, IEnvironmentConnectionService connection)
    {
        Environments = environments;
        Connection = connection;
    }

    protected async Task<IReadOnlyList<DockerEnvironment>> TargetsAsync(ScheduledTask task, CancellationToken ct)
    {
        if (task.EnvironmentId is > 0)
        {
            var env = await Environments.GetAsync(task.EnvironmentId.Value, ct);
            return env is { IsEnabled: true } ? new[] { env } : Array.Empty<DockerEnvironment>();
        }

        return await Environments.GetEnabledAsync(ct);
    }
}

/// <summary>Removes unused volumes (local by default; network shares only when opted in) per target environment.</summary>
public sealed class PruneVolumesAction : ScheduleActionBase, IScheduleAction
{
    public PruneVolumesAction(EnvironmentService e, IEnvironmentConnectionService c) : base(e, c) { }

    public ScheduleAction Type => ScheduleAction.PruneVolumes;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var opt = ScheduleOptions.Parse(task.OptionsJson);
        var envs = await TargetsAsync(task, ct);
        if (envs.Count == 0) { return (false, "No target environment."); }

        var removed = 0; var errors = new List<string>();
        foreach (var env in envs)
        {
            try
            {
                var settings = Environments.BuildSettings(env);
                var unused = await Connection.ListUnusedVolumesDetailedAsync(settings, ct);
                var names = unused.Where(u => opt.IncludeShares || !u.IsNetworkShare).Select(u => u.Name).ToList();
                if (names.Count > 0)
                {
                    var result = await Connection.RemoveVolumesAsync(settings, names, ct);
                    removed += result.Removed;
                }
            }
            catch (Exception ex) { errors.Add($"{env.Name}: {ex.Message}"); }
        }

        return Result($"Pruned {removed} volume(s) across {envs.Count} environment(s).", errors);
    }

    internal static (bool, string) Result(string summary, List<string> errors)
        => errors.Count == 0 ? (true, summary) : (false, summary + " Errors: " + string.Join("; ", errors));
}

/// <summary>Prunes dangling (or all unused) images per target environment.</summary>
public sealed class PruneImagesAction : ScheduleActionBase, IScheduleAction
{
    public PruneImagesAction(EnvironmentService e, IEnvironmentConnectionService c) : base(e, c) { }

    public ScheduleAction Type => ScheduleAction.PruneImages;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var opt = ScheduleOptions.Parse(task.OptionsJson);
        var envs = await TargetsAsync(task, ct);
        if (envs.Count == 0) { return (false, "No target environment."); }

        var removed = 0; var errors = new List<string>();
        foreach (var env in envs)
        {
            try
            {
                var result = await Connection.PruneImagesAsync(Environments.BuildSettings(env), opt.All, ct);
                if (result.Success) { removed += result.Removed; }
                else { errors.Add($"{env.Name}: {result.Detail}"); }
            }
            catch (Exception ex) { errors.Add($"{env.Name}: {ex.Message}"); }
        }

        return PruneVolumesAction.Result($"Pruned {removed} image(s) across {envs.Count} environment(s).", errors);
    }
}

/// <summary>Prunes unused custom networks per target environment.</summary>
public sealed class PruneNetworksAction : ScheduleActionBase, IScheduleAction
{
    public PruneNetworksAction(EnvironmentService e, IEnvironmentConnectionService c) : base(e, c) { }

    public ScheduleAction Type => ScheduleAction.PruneNetworks;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var envs = await TargetsAsync(task, ct);
        if (envs.Count == 0) { return (false, "No target environment."); }

        var removed = 0; var errors = new List<string>();
        foreach (var env in envs)
        {
            try
            {
                var result = await Connection.PruneNetworksAsync(Environments.BuildSettings(env), ct);
                if (result.Success) { removed += result.Removed; }
                else { errors.Add($"{env.Name}: {result.Detail}"); }
            }
            catch (Exception ex) { errors.Add($"{env.Name}: {ex.Message}"); }
        }

        return PruneVolumesAction.Result($"Pruned {removed} network(s) across {envs.Count} environment(s).", errors);
    }
}

/// <summary>Sends a status summary (per-environment running/CPU/RAM/Disk) via the notification channels.</summary>
public sealed class SummaryAction : ScheduleActionBase, IScheduleAction
{
    private readonly INotificationService _notifications;

    public SummaryAction(EnvironmentService e, IEnvironmentConnectionService c, INotificationService notifications) : base(e, c)
        => _notifications = notifications;

    public ScheduleAction Type => ScheduleAction.Summary;

    public async Task<(bool Ok, string Summary)> ExecuteAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var envs = await TargetsAsync(task, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"MatDock status summary — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        foreach (var env in envs)
        {
            try
            {
                var st = await Connection.GetHostStatsAsync(Environments.BuildSettings(env), ct);
                sb.AppendLine($"- {env.Name}: {st.RunningContainers ?? 0} running · CPU {st.CpuPercent:0}% · RAM {st.MemPercent:0}% · Disk {st.DiskPercent:0}%");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"- {env.Name}: unreachable ({ex.Message})");
            }
        }

        var (ok, message) = await _notifications.NotifyAsync("MatDock status summary", sb.ToString(), ct);
        return ok ? (true, $"Summary sent for {envs.Count} environment(s).") : (false, message);
    }
}
