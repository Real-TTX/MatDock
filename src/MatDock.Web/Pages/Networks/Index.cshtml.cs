using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Pages.Networks;

public class IndexModel : PageModel
{
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;
    private readonly IMemoryCache _cache;

    public IndexModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService, IMemoryCache cache)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
        _cache = cache;
    }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public long EnvId { get; set; }
    [BindProperty(SupportsGet = true)] public bool Refresh { get; set; }

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<NetworkRow> Rows { get; private set; } = new();
    public List<(string Environment, string Message)> Errors { get; private set; } = new();

    public sealed record NetworkRow(DockerEnvironment Environment, DockerNetwork Network, bool Unused);

    private sealed record CachedNetworks(IReadOnlyList<DockerNetwork> Networks, IReadOnlyCollection<string> Unused, string? Error);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRemoveAsync(long envId, string name)
    {
        if (!User.IsInRole(nameof(UserRole.Admin)))
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(new { EnvId, Q });
        }

        var (ok, message) = await _connectionService.RemoveNetworkAsync(
            _environmentService.BuildSettings(env), name ?? string.Empty, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        // Bypass the cache so the removed network disappears immediately.
        return RedirectToPage(new { EnvId, Q, Refresh = true });
    }

    private async Task LoadAsync()
    {
        EnvId = MatDock.Web.Support.EnvSelection.Resolve(HttpContext) ?? 0;
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);

        if (EnvId > 0 && Environments.All(e => e.Id != EnvId))
        {
            EnvId = 0;
            MatDock.Web.Support.EnvSelection.Clear(HttpContext);
        }

        var targets = (EnvId > 0 ? Environments.Where(e => e.Id == EnvId) : Environments).ToList();
        var prepared = targets.Select(e => (Env: e, Settings: _environmentService.BuildSettings(e))).ToList();

        using var gate = new SemaphoreSlim(4);
        var tasks = prepared.Select(async p =>
        {
            var cacheKey = $"networks:{p.Env.Id}";
            if (!Refresh && _cache.TryGetValue(cacheKey, out CachedNetworks? cached) && cached is not null)
            {
                return (p.Env, cached);
            }

            await gate.WaitAsync(HttpContext.RequestAborted);
            try
            {
                CachedNetworks entry;
                try
                {
                    var (networks, unused) = await _connectionService.ListNetworksWithUsageAsync(p.Settings, HttpContext.RequestAborted);
                    entry = new CachedNetworks(networks, unused, null);
                }
                catch (Exception ex)
                {
                    entry = new CachedNetworks(Array.Empty<DockerNetwork>(), Array.Empty<string>(), ex.Message);
                }

                _cache.Set(cacheKey, entry, CacheTtl);
                return (p.Env, entry);
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        var rows = new List<NetworkRow>();
        foreach (var (env, entry) in results)
        {
            if (entry.Error is not null)
            {
                Errors.Add((env.Name, entry.Error));
                continue;
            }

            var unused = entry.Unused as IReadOnlySet<string> ?? new HashSet<string>(entry.Unused, StringComparer.Ordinal);
            rows.AddRange(entry.Networks.Select(n => new NetworkRow(env, n, unused.Contains(n.Name))));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            rows = rows.Where(r =>
                r.Network.Name.Contains(s, Ic) ||
                r.Network.Driver.Contains(s, Ic) ||
                r.Environment.Name.Contains(s, Ic)).ToList();
        }

        Rows = rows
            .OrderBy(r => r.Environment.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Network.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
