using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
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
    private readonly OperationRegistry _operations;

    public IndexModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService, ContainerService containerService, IMemoryCache cache,
        OperationRegistry operations)
    {
        _operations = operations;
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

    /// <summary>The pull started from this page (its id in the address), shown with its progress while it runs and its outcome after.</summary>
    [BindProperty(SupportsGet = true)] public string? Op { get; set; }

    public Operation? Running { get; private set; }

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<ImageRow> Rows { get; private set; } = new();
    public List<(string Environment, string Message)> Errors { get; private set; } = new();

    public sealed record ImageRow(DockerEnvironment Environment, DockerImage Image, bool InUse, IReadOnlyList<string> UsedBy);

    private sealed record CachedImages(IReadOnlyList<DockerImage> Images, IReadOnlyList<DockerContainer> Containers, string? Error);

    public async Task OnGetAsync()
    {
        Running = string.IsNullOrEmpty(Op) ? null : _operations.Get(Op);
        await LoadAsync();
    }

    /// <summary>Deletes the ticked images (admins); one that a container uses is refused by docker and named in the answer.</summary>
    public async Task<IActionResult> OnPostBulkAsync(string? bulkAction, List<string>? ids)
    {
        if (!User.IsInRole(nameof(UserRole.Admin)) || bulkAction != "Remove")
        {
            return Forbid();
        }

        var ct = HttpContext.RequestAborted;
        var environments = (await _environmentService.GetEnabledAsync(ct)).ToDictionary(e => e.Id);
        var results = new List<(string Name, bool Ok, string Message)>();
        foreach (var (envId, reference) in MatDock.Web.Support.Bulk.Parse(ids))
        {
            var (ok, message) = environments.TryGetValue(envId, out var env)
                ? await _connectionService.RemoveImageAsync(_environmentService.BuildSettings(env), reference, force: false, ct)
                : (false, "environment not available");
            results.Add((reference, ok, message));
        }

        (StatusMessage, IsError) = MatDock.Web.Support.Bulk.Summary("Delete", results);
        return RedirectToPage(new { EnvId, Q, Refresh = true });
    }

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

    /// <summary>Pulls an image by reference onto the selected environment.</summary>
    public async Task<IActionResult> OnPostPullAsync(long envId, string reference)
    {
        if (!User.IsInRole(nameof(UserRole.Admin)))
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Select a specific environment to pull an image.";
            IsError = true;
            return RedirectToPage(new { EnvId, Q });
        }

        reference = (reference ?? string.Empty).Trim();
        if (!MatDock.Core.Images.ImageCommands.IsValidImageRef(reference))
        {
            StatusMessage = "Invalid image reference.";
            IsError = true;
            return RedirectToPage(new { EnvId, Q });
        }

        // In the background: the page shows the download as it goes (live), and a pull of gigabytes does not hang on the request.
        var settings = _environmentService.BuildSettings(env);
        var label = $"{reference} → {env.Name}";
        var operation = _operations.Start("image-pull", label, $"image-pull:{env.Id}:{reference}", async ctx =>
        {
            ctx.Operation.Steps.Add("pull", label);
            ctx.Operation.Steps.Set("pull", StepState.Running);
            var connection = ctx.Services.GetRequiredService<IEnvironmentConnectionService>();
            bool ok;
            string message;
            using (MatDock.Core.Execution.CommandOutput.Redirect(ctx.Operation.Append))
            {
                (ok, message) = await connection.PullImageAsync(settings, reference, ctx.CancellationToken);
            }

            ctx.Operation.Steps.Set("pull", ok ? StepState.Done : StepState.Failed, ok ? null : message);
            ctx.Services.GetRequiredService<IMemoryCache>().Remove($"images:{env.Id}");
            return ok ? OperationOutcome.Success() : OperationOutcome.Failure(message);
        });
        return RedirectToPage(new { EnvId = envId, Q, op = operation.Id });
    }

    /// <summary>Prunes the dangling images, or with <paramref name="all"/> every image no container uses.</summary>
    public async Task<IActionResult> OnPostPruneAsync(long envId, bool all = false)
    {
        if (!User.IsInRole(nameof(UserRole.Admin)))
        {
            return Forbid();
        }

        // A specific environment prunes just that host; "all" (envId <= 0) prunes every enabled host.
        var enabled = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        var targets = envId > 0 ? enabled.Where(e => e.Id == envId).ToList() : enabled;
        if (targets.Count == 0)
        {
            StatusMessage = "No environment available to prune.";
            IsError = true;
            return RedirectToPage(new { EnvId, Q });
        }

        var removed = 0;
        var errors = new List<string>();
        var reclaimed = new List<string>();
        var anyOk = false;
        foreach (var env in targets)
        {
            try
            {
                var result = await _connectionService.PruneImagesAsync(
                    _environmentService.BuildSettings(env), all, HttpContext.RequestAborted);
                if (result.Success)
                {
                    removed += result.Removed;
                    anyOk = true;
                    if (!string.IsNullOrEmpty(result.Reclaimed)) { reclaimed.Add(result.Reclaimed); }
                }
                else { errors.Add($"{env.Name}: {result.Detail}"); }
            }
            catch (Exception ex)
            {
                errors.Add($"{env.Name}: {ex.Message}");
            }
        }

        var freed = reclaimed.Count > 0 ? $", freed {string.Join(" + ", reclaimed)}" : "";
        StatusMessage = errors.Count == 0
            ? $"Pruned {removed} {(all ? "unused" : "dangling")} image(s){freed}."
            : $"Pruned {removed} {(all ? "unused" : "dangling")} image(s){freed}; errors: {string.Join(" · ", errors)}";
        IsError = !anyOk && errors.Count > 0;
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
