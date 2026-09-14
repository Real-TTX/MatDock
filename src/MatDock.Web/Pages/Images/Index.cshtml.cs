using MatDock.Core.Containers;
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
    private readonly ContainerService _containerService;
    private readonly IMemoryCache _cache;

    public IndexModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService, ContainerService containerService, IMemoryCache cache)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
        _containerService = containerService;
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

    public sealed record ImageRow(DockerEnvironment Environment, DockerImage Image, bool InUse, IReadOnlyList<string> UsedBy);

    private sealed record CachedImages(IReadOnlyList<DockerImage> Images, IReadOnlyList<DockerContainer> Containers, string? Error);

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
                    // Containers (no stats — we only need their image refs to flag Used/Unused). Best-effort.
                    IReadOnlyList<DockerContainer> containers;
                    try
                    {
                        containers = await _containerService.ListAsync(p.Env, HttpContext.RequestAborted, includeStats: false);
                    }
                    catch
                    {
                        containers = Array.Empty<DockerContainer>();
                    }

                    entry = new CachedImages(images, containers, null);
                }
                catch (Exception ex)
                {
                    entry = new CachedImages(Array.Empty<DockerImage>(), Array.Empty<DockerContainer>(), ex.Message);
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

            foreach (var img in entry.Images)
            {
                var usedBy = entry.Containers
                    .Where(c => Uses(c, img))
                    .Select(c => c.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                rows.Add(new ImageRow(env, img, usedBy.Count > 0, usedBy));
            }
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

    // Whether a container runs on a given image. docker ps reports the image as repo:tag, repo@sha256,
    // or a (short) id for untagged images — so we match on all three.
    private static bool Uses(DockerContainer c, DockerImage img)
    {
        var ci = c.Image?.Trim() ?? string.Empty;
        if (ci.Length == 0)
        {
            return false;
        }

        if (!img.Dangling)
        {
            if (string.Equals(ci, img.Reference, Ic))
            {
                return true;
            }

            // "repo" with no tag implies :latest.
            if (string.Equals(img.Tag, "latest", Ic) && string.Equals(ci, img.Repository, Ic))
            {
                return true;
            }
        }

        if (img.Digest is { Length: > 0 } dg && ci.Contains(dg, StringComparison.Ordinal))
        {
            return true;
        }

        if (img.Id.Length > 0)
        {
            var shortId = img.Id.Length > 12 ? img.Id[..12] : img.Id;
            if (ci.Contains(shortId, Ic) || (shortId.Length > 0 && shortId.Contains(ci, Ic) && ci.Length >= 6))
            {
                return true;
            }
        }

        return false;
    }
}
