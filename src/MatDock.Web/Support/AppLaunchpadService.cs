using MatDock.Core.Apps;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using Microsoft.Extensions.Caching.Memory;

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
/// shared by the Launchpad page and the Dashboard. Reuses the Dashboard/Stacks container cache.
/// </summary>
public sealed class AppLaunchpadService
{
    private readonly StackService _stacks;
    private readonly EnvironmentService _environments;
    private readonly ContainerService _containers;
    private readonly IMemoryCache _cache;

    public AppLaunchpadService(StackService stacks, EnvironmentService environments, ContainerService containers, IMemoryCache cache)
    {
        _stacks = stacks;
        _environments = environments;
        _containers = containers;
        _cache = cache;
    }

    /// <summary>Tiles for apps in scope (<paramref name="envId"/> null/0 = all environments).</summary>
    public async Task<List<AppTile>> GetTilesAsync(long? envId, CancellationToken ct = default)
    {
        var allEnvs = await _environments.GetAllAsync(ct);
        var envById = allEnvs.ToDictionary(e => e.Id);

        var scope = envId is > 0 ? envId.Value : 0;
        var managed = (await _stacks.GetAllAsync(ct))
            .Where(s => !string.IsNullOrWhiteSpace(s.AppMetaJson) && (scope <= 0 || s.EnvironmentId == scope))
            .ToList();
        if (managed.Count == 0)
        {
            return new List<AppTile>();
        }

        var containersByEnv = new Dictionary<long, IReadOnlyList<DockerContainer>>();
        using var gate = new SemaphoreSlim(4);
        var envIds = managed.Select(s => s.EnvironmentId).Distinct()
            .Where(id => envById.TryGetValue(id, out var e) && e.IsEnabled);

        var scans = envIds.Select(async id =>
        {
            if (_cache.TryGetValue($"dash:containers:{id}", out IReadOnlyList<DockerContainer>? cached) && cached is not null)
            {
                return (id, cached);
            }

            await gate.WaitAsync(ct);
            try
            {
                IReadOnlyList<DockerContainer> list;
                try { list = await _containers.ListAsync(envById[id], ct, includeStats: false); }
                catch { list = Array.Empty<DockerContainer>(); }
                _cache.Set($"dash:containers:{id}", list, TimeSpan.FromSeconds(30));
                return (id, list);
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var (id, list) in await Task.WhenAll(scans))
        {
            containersByEnv[id] = list;
        }

        var tiles = new List<AppTile>();
        foreach (var stack in managed)
        {
            var meta = AppMetadataParser.FromJson(stack.AppMetaJson);
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
