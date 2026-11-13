using MatDock.Core.Apps;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;

namespace MatDock.Web.Support;

/// <summary>A deployed app tile: a managed stack that carries MatDock app metadata, with its live status.</summary>
public sealed record AppTile(
    string Name, AppMetadata Meta, string EnvName, long EnvId,
    string? Host, int? Port, string? OpenUrl, int Running, int Total)
{
    public string StatusKind => Total == 0 ? "idle" : Running == Total ? "running" : Running > 0 ? "partial" : "stopped";
}

/// <summary>
/// Builds the app launchpad tiles (managed stacks with app metadata + their live container status),
/// shared by the Launchpad page and the Dashboard. Reads the containers as the dashboard does (<see cref="HostReadings"/>).
/// </summary>
public sealed class AppLaunchpadService
{
    private readonly StackService _stacks;
    private readonly EnvironmentService _environments;
    private readonly HostReadings _readings;

    public AppLaunchpadService(StackService stacks, EnvironmentService environments, HostReadings readings)
    {
        _stacks = stacks;
        _environments = environments;
        _readings = readings;
    }

    /// <summary>Tiles for apps in scope (<paramref name="envId"/> null/0 = all environments).</summary>
    public async Task<List<AppTile>> GetTilesAsync(long? envId, CancellationToken ct = default)
    {
        var allEnvs = await _environments.GetAllAsync(ct);
        var envById = allEnvs.ToDictionary(e => e.Id);

        var scope = envId is > 0 ? envId.Value : 0;
        // allEnvs holds only what the current user may see, so stacks of other environments drop out here.
        var managed = (await _stacks.GetAllAsync(ct))
            .Where(s => (!string.IsNullOrWhiteSpace(s.AppMetaJson) || !string.IsNullOrWhiteSpace(s.Icon)) && (scope <= 0 || s.EnvironmentId == scope) && envById.ContainsKey(s.EnvironmentId))
            .ToList();
        if (managed.Count == 0)
        {
            return new List<AppTile>();
        }

        var containersByEnv = new Dictionary<long, IReadOnlyList<DockerContainer>>();
        var envIds = managed.Select(s => s.EnvironmentId).Distinct()
            .Where(id => envById.TryGetValue(id, out var e) && e.IsEnabled);

        // The list the dashboard reads too: the last one while a new one is taken, so the tiles do not wait for every host.
        var scans = envIds.Select(async id => (id, await _readings.ContainersAsync(envById[id], ct)));

        foreach (var (id, list) in await Task.WhenAll(scans))
        {
            containersByEnv[id] = list;
        }

        var tiles = new List<AppTile>();
        foreach (var stack in managed)
        {
            // The icon chosen in MatDock wins over the compose file's.
            var meta = AppMetadataParser.FromJson(AppMetadataParser.WithIcon(stack.AppMetaJson, stack.Icon));
            if (meta is null || meta.IsEmpty)
            {
                continue;
            }

            var env = envById.TryGetValue(stack.EnvironmentId, out var e) ? e : null;
            var host = env is not null ? OpenUrls.PortLinkHost(env) : null;
            var containers = containersByEnv.TryGetValue(stack.EnvironmentId, out var cs)
                ? cs.Where(c => string.Equals(c.Project, stack.Name, StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<DockerContainer>();

            var port = AppView.PrimaryPort(containers);
            var over = containers.Select(c => c.OpenUrlOverride).FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));
            var open = AppView.OpenHref(meta, over, env?.BaseUrl, host, port);

            tiles.Add(new AppTile(
                string.IsNullOrWhiteSpace(meta.Name) ? stack.Name : meta.Name!,
                meta, env?.Name ?? $"#{stack.EnvironmentId}", stack.EnvironmentId,
                host, port, open,
                containers.Count(c => c.IsRunning), containers.Count));
        }

        return tiles
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
