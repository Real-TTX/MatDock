using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Pages.Images;

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
    public List<ImageRow> Rows { get; private set; } = new();
    public List<(string Environment, string Message)> Errors { get; private set; } = new();

    public sealed record ImageRow(DockerEnvironment Environment, DockerImage Image);

    private sealed record CachedImages(IReadOnlyList<DockerImage> Images, string? Error);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRemoveAsync(long envId, string reference)
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

        var (ok, message) = await _connectionService.RemoveImageAsync(
            _environmentService.BuildSettings(env), reference ?? string.Empty, force: false, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage(new { EnvId, Q, Refresh = true });
    }

    public async Task<IActionResult> OnPostPruneAsync(long envId)
    {
        if (!User.IsInRole(nameof(UserRole.Admin)))
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Select a specific environment to prune its images.";
            IsError = true;
            return RedirectToPage(new { EnvId, Q });
        }

        var result = await _connectionService.PruneImagesAsync(
            _environmentService.BuildSettings(env), all: false, HttpContext.RequestAborted);
        StatusMessage = result.Success ? $"Pruned {result.Removed} dangling image(s)." : result.Detail;
        IsError = !result.Success;
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
            var cacheKey = $"images:{p.Env.Id}";
            if (!Refresh && _cache.TryGetValue(cacheKey, out CachedImages? cached) && cached is not null)
            {
                return (p.Env, cached);
            }

            await gate.WaitAsync(HttpContext.RequestAborted);
            try
            {
                CachedImages entry;
                try
                {
                    var images = await _connectionService.ListImagesAsync(p.Settings, HttpContext.RequestAborted);
                    entry = new CachedImages(images, null);
                }
                catch (Exception ex)
                {
                    entry = new CachedImages(Array.Empty<DockerImage>(), ex.Message);
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

        var rows = new List<ImageRow>();
        foreach (var (env, entry) in results)
        {
            if (entry.Error is not null)
            {
                Errors.Add((env.Name, entry.Error));
                continue;
            }

            rows.AddRange(entry.Images.Select(i => new ImageRow(env, i)));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            rows = rows.Where(r =>
                r.Image.Repository.Contains(s, Ic) ||
                r.Image.Tag.Contains(s, Ic) ||
                r.Image.Id.Contains(s, Ic) ||
                r.Environment.Name.Contains(s, Ic)).ToList();
        }

        Rows = rows
            .OrderBy(r => r.Environment.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Image.Dangling)
            .ThenBy(r => r.Image.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Image.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
