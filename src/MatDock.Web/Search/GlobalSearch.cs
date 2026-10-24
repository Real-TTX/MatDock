using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Search;

/// <summary>One hit of the search, as the field of the header bar (MatUi data-search-url) wants it.</summary>
public sealed record SearchItem(string Title, string? Text, string Href, string Icon, string Kind);

public sealed record SearchGroup(string Name, IReadOnlyList<SearchItem> Items);

/// <summary>
/// The search of the header bar: containers, compose stacks, images, volumes and networks of the environments the user sees, the environments
/// themselves, the managed stacks (admins) and the pages of the menu. What a host holds is asked once and kept for <see cref="Ttl"/>, so typing does not
/// open a connection per key; a host that does not answer simply has no hits.
/// </summary>
public sealed class GlobalSearch
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private sealed record Snapshot(IReadOnlyList<DockerContainer> Containers, IReadOnlyList<DockerImage> Images, IReadOnlyList<DockerVolume> Volumes, IReadOnlyList<DockerNetwork> Networks);

    private readonly EnvironmentService _environments;
    private readonly ContainerService _containers;
    private readonly IEnvironmentConnectionService _connection;
    private readonly StackService _stacks;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GlobalSearch> _logger;

    public GlobalSearch(EnvironmentService environments, ContainerService containers, IEnvironmentConnectionService connection, StackService stacks, IMemoryCache cache, ILogger<GlobalSearch> logger)
    {
        _environments = environments;
        _containers = containers;
        _connection = connection;
        _stacks = stacks;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>The pages of the menu: everyone's, and the admins' (name, address, icon).</summary>
    private static readonly (string Name, string Href, string Icon, bool Admin)[] Pages =
    [
        ("Dashboard", "/", "dashboard", false),
        ("Environments", "/Environments", "server", false),
        ("Apps", "/Launchpad", "apps", false),
        ("Containers", "/Containers", "box", false),
        ("Images", "/Images", "image", false),
        ("Volumes", "/Volumes", "volume", false),
        ("Networks", "/Networks", "network", false),
        ("Network map", "/Networks/Map", "network", false),
        ("Events", "/Events", "activity", false),
        ("Profile", "/Account/Profile", "user", false),
        ("Stacks", "/Stacks", "layers", true),
        ("Sync", "/Stacks/SyncJobs", "refresh", true),
        ("Updates", "/Updates", "arrow-up", true),
        ("Update rules", "/Updates/Rules", "sliders", true),
        ("Schedules", "/Schedules", "clock", true),
        ("Backups", "/Backups", "save", true),
        ("App templates", "/Apps", "template", true),
        ("Shell", "/Terminal", "terminal", true),
        ("System settings", "/Settings", "settings", true),
        ("Users", "/Users", "users", true),
        ("Notifications", "/Settings/Notifications", "bell", true),
        ("Registries", "/Settings/Registries", "registry", true),
        ("Backup targets", "/Settings?tab=backup", "save", true),
        ("MatDock backup", "/Settings/SelfBackup", "database", true),
        ("Activity", "/Settings/Activity", "clock", true),
        ("About", "/About", "info", false),
    ];

    /// <summary>The groups with hits for <paramref name="query"/>, at most <paramref name="perGroup"/> each; the best (starts with the text) first.</summary>
    public async Task<IReadOnlyList<SearchGroup>> SearchAsync(string? query, bool admin, int perGroup, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? string.Empty;
        if (q.Length == 0)
        {
            return [];
        }

        var environments = await _environments.GetEnabledAsync(ct);
        using var gate = new SemaphoreSlim(4);
        var snapshots = await Task.WhenAll(environments.Select(async env =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return (env, snapshot: await SnapshotAsync(env, ct));
            }
            finally
            {
                gate.Release();
            }
        }));

        var many = environments.Count > 1;
        string Where(DockerEnvironment env, string? text) => many ? (text is null ? env.Name : $"{env.Name} · {text}") : text ?? env.Name;

        var containers = new List<(string Key, SearchItem Item)>();
        var projects = new Dictionary<(long, string), SearchItem>();
        var images = new List<(string Key, SearchItem Item)>();
        var volumes = new List<(string Key, SearchItem Item)>();
        var networks = new List<(string Key, SearchItem Item)>();
        foreach (var (env, s) in snapshots)
        {
            foreach (var c in s.Containers)
            {
                if (Hit(q, c.Name, c.Image, c.Project, c.Service))
                {
                    containers.Add((c.Name, new SearchItem(c.Name, Where(env, $"{c.Image} · {c.State}"), $"/Containers/ContainerDetail?envId={env.Id}&id={Uri.EscapeDataString(c.Id)}", "box", "container")));
                }

                if (c.Project is { Length: > 0 } project && Hit(q, project) && !projects.ContainsKey((env.Id, project)))
                {
                    projects[(env.Id, project)] = new SearchItem(project, Where(env, "compose stack"), $"/Containers/Stack?envId={env.Id}&project={Uri.EscapeDataString(project)}", "layers", "stack");
                }
            }

            foreach (var i in s.Images.Where(i => !i.Dangling && Hit(q, i.Reference)))
            {
                images.Add((i.Reference, new SearchItem(i.Reference, Where(env, i.Size), $"/Images/Detail?envId={env.Id}&reference={Uri.EscapeDataString(i.Reference)}", "image", "image")));
            }

            foreach (var v in s.Volumes.Where(v => Hit(q, v.Name)))
            {
                volumes.Add((v.Name, new SearchItem(v.Name, Where(env, v.Driver), $"/Volumes/Detail?envId={env.Id}&volume={Uri.EscapeDataString(v.Name)}", "volume", "volume")));
            }

            foreach (var n in s.Networks.Where(n => Hit(q, n.Name)))
            {
                networks.Add((n.Name, new SearchItem(n.Name, Where(env, n.Driver), $"/Networks/Detail?envId={env.Id}&network={Uri.EscapeDataString(n.Name)}", "network", "network")));
            }
        }

        var stacks = projects.Select(p => (p.Key.Item2, p.Value)).ToList();
        if (admin)
        {
            var names = environments.ToDictionary(e => e.Id, e => e.Name);
            foreach (var stack in (await _stacks.GetAllAsync(ct)).Where(s => Hit(q, s.Name) && names.ContainsKey(s.EnvironmentId)))
            {
                stacks.RemoveAll(s => s.Item2.Href == $"/Containers/Stack?envId={stack.EnvironmentId}&project={Uri.EscapeDataString(stack.Name)}");
                stacks.Add((stack.Name, new SearchItem(stack.Name, many ? $"{names[stack.EnvironmentId]} · managed stack" : "managed stack", $"/Stacks/Edit?id={stack.Id}", "layers", "stack")));
            }
        }

        var envHits = environments.Where(e => Hit(q, e.Name, e.Host))
            .Select(e => (e.Name, new SearchItem(e.Name, e.ConnectionType == ConnectionType.Local ? "local Docker" : e.Host, $"/Environments/Detail?id={e.Id}", "server", "environment")))
            .ToList();
        var pages = Pages.Where(p => (admin || !p.Admin) && Hit(q, p.Name))
            .Select(p => (p.Name, new SearchItem(p.Name, null, p.Href, p.Icon, "page")))
            .ToList();

        return new[]
            {
                ("Containers", containers), ("Stacks", stacks), ("Images", images), ("Volumes", volumes), ("Networks", networks),
                ("Environments", envHits), ("Pages", pages),
            }
            .Select(g => new SearchGroup(g.Item1, Best(q, g.Item2, perGroup)))
            .Where(g => g.Items.Count > 0)
            .ToList();
    }

    private async Task<Snapshot> SnapshotAsync(DockerEnvironment env, CancellationToken ct)
    {
        var key = "search:" + env.Id;
        if (_cache.TryGetValue(key, out Snapshot? cached) && cached is not null)
        {
            return cached;
        }

        var settings = _environments.BuildSettings(env);
        async Task<T> Safe<T>(Func<Task<T>> ask, T none)
        {
            try
            {
                return await ask();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Search could not read env {Env}.", env.Id);
                return none;
            }
        }

        var snapshot = new Snapshot(
            await Safe(() => _containers.ListAsync(env, ct, includeStats: false), (IReadOnlyList<DockerContainer>)[]),
            await Safe(() => _connection.ListImagesAsync(settings, ct), (IReadOnlyList<DockerImage>)[]),
            await Safe(async () => (await _connection.ListVolumesWithUsageAsync(settings, ct)).Volumes, (IReadOnlyList<DockerVolume>)[]),
            await Safe(async () => (await _connection.ListNetworksWithUsageAsync(settings, ct)).Networks, (IReadOnlyList<DockerNetwork>)[]));
        _cache.Set(key, snapshot, Ttl);
        return snapshot;
    }

    private static bool Hit(string q, params string?[] values) => values.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Those whose name starts with the text first, then the shorter names, at most <paramref name="take"/>.</summary>
    private static IReadOnlyList<SearchItem> Best(string q, List<(string Key, SearchItem Item)> hits, int take)
        => hits.OrderBy(h => h.Key.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(h => h.Key.Length)
            .ThenBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(h => h.Item)
            .ToList();
}
