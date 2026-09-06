using MatDock.Core.Apps;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Pages.Launchpad;

/// <summary>App launchpad: managed stacks that carry MatDock app metadata, as a tile grid grouped by category.</summary>
public class IndexModel : PageModel
{
    private readonly StackService _stackService;
    private readonly EnvironmentService _environmentService;
    private readonly ContainerService _containerService;
    private readonly IMemoryCache _cache;

    public IndexModel(StackService stackService, EnvironmentService environmentService, ContainerService containerService, IMemoryCache cache)
    {
        _stackService = stackService;
        _environmentService = environmentService;
        _containerService = containerService;
        _cache = cache;
    }

    public long EnvId { get; private set; }
    public List<CategoryGroup> Groups { get; private set; } = new();
    public int TotalApps { get; private set; }
    public List<string> LoadErrors { get; private set; } = new();

    public sealed record AppTile(string Name, AppMetadata Meta, string EnvName, string? Host, int? Port, string? OpenUrl);
    public sealed record CategoryGroup(string Category, List<AppTile> Apps);

    public async Task OnGetAsync()
    {
        EnvId = EnvSelection.Resolve(HttpContext) ?? 0;

        var allEnvs = await _environmentService.GetAllAsync(HttpContext.RequestAborted);
        var envById = allEnvs.ToDictionary(e => e.Id);
        if (EnvId > 0 && !allEnvs.Any(e => e.Id == EnvId && e.IsEnabled))
        {
            EnvId = 0;
            EnvSelection.Clear(HttpContext);
        }

        var managed = await _stackService.GetAllAsync(HttpContext.RequestAborted);
        managed = managed.Where(s => !string.IsNullOrWhiteSpace(s.AppMetaJson)
                                     && (EnvId <= 0 || s.EnvironmentId == EnvId)).ToList();
        if (managed.Count == 0)
        {
            return;
        }

        // Container ports/open-links per environment (shared cache with Dashboard/Stacks).
        var envIds = managed.Select(s => s.EnvironmentId).Distinct().ToList();
        var containersByEnv = new Dictionary<long, IReadOnlyList<DockerContainer>>();
        using var gate = new SemaphoreSlim(4);
        var scans = envIds.Where(id => envById.TryGetValue(id, out var e) && e.IsEnabled).Select(async id =>
        {
            var env = envById[id];
            if (_cache.TryGetValue($"dash:containers:{id}", out IReadOnlyList<DockerContainer>? cached) && cached is not null)
            {
                return (id, cached, error: (string?)null);
            }
            await gate.WaitAsync(HttpContext.RequestAborted);
            try
            {
                var list = await _containerService.ListAsync(env, HttpContext.RequestAborted, includeStats: false);
                _cache.Set($"dash:containers:{id}", list, TimeSpan.FromSeconds(30));
                return (id, list, error: (string?)null);
            }
            catch (Exception ex)
            {
                return (id, (IReadOnlyList<DockerContainer>)Array.Empty<DockerContainer>(), error: $"{env.Name}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var (id, list, error) in await Task.WhenAll(scans))
        {
            if (error is not null)
            {
                LoadErrors.Add(error);
            }
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
            var open = OpenUrls.Resolve(over, env?.BaseUrl)
                       ?? (host is not null && port is not null ? $"http://{host}:{port}" : null);

            tiles.Add(new AppTile(
                string.IsNullOrWhiteSpace(meta.Name) ? stack.Name : meta.Name!,
                meta, env?.Name ?? $"#{stack.EnvironmentId}", host, port, open));
        }

        TotalApps = tiles.Count;
        Groups = tiles
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Meta.Category) ? "Apps" : t.Meta.Category!)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CategoryGroup(g.Key, g.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }
}
