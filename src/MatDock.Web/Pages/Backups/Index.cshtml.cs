using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Backups;

public class IndexModel : PageModel
{
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly VolumeBackupService _backupService;
    private readonly BundleBackupService _bundleService;
    private readonly EnvironmentService _environments;

    public IndexModel(VolumeBackupService backupService, BundleBackupService bundleService, EnvironmentService environments)
    {
        _backupService = backupService;
        _bundleService = bundleService;
        _environments = environments;
    }

    /// <summary>Full stack backups (bundles), newest first, filtered like the volume history.</summary>
    public List<StackBackup> Bundles { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Volume { get; set; }

    /// <summary>Filter by source environment name; empty = all.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Env { get; set; }

    public List<VolumeBackup> Items { get; private set; } = new();

    /// <summary>Distinct source-environment names across all backups (for the filter dropdown).</summary>
    public List<string> EnvNames { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        var all = await _backupService.GetAllAsync(HttpContext.RequestAborted);
        EnvNames = all.Select(b => b.SourceEnvironmentName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IEnumerable<VolumeBackup> query = all;

        if (!string.IsNullOrWhiteSpace(Volume))
        {
            query = query.Where(b => b.VolumeName.Equals(Volume, Ic));
        }

        if (!string.IsNullOrWhiteSpace(Env))
        {
            query = query.Where(b => b.SourceEnvironmentName.Equals(Env, Ic));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            query = query.Where(b => b.VolumeName.Contains(s, Ic) || b.SourceEnvironmentName.Contains(s, Ic));
        }

        Items = query.ToList();

        IEnumerable<StackBackup> bundles = await _bundleService.GetBundlesAsync(ct: HttpContext.RequestAborted);
        if (!string.IsNullOrWhiteSpace(Env))
        {
            bundles = bundles.Where(b => b.EnvironmentName.Equals(Env, Ic));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            bundles = bundles.Where(b => b.StackName.Contains(s, Ic) || b.EnvironmentName.Contains(s, Ic));
        }

        Bundles = string.IsNullOrWhiteSpace(Volume) ? bundles.ToList() : new List<StackBackup>();
    }

    /// <summary>Restores a stack bundle into its environment (the current state is backed up first).</summary>
    public async Task<IActionResult> OnPostRestoreBundleAsync(long id)
    {
        var ct = HttpContext.RequestAborted;
        var bundle = await _bundleService.GetBundleAsync(id, ct);
        var env = bundle is null ? null : await _environments.GetAsync(bundle.EnvironmentId, ct);
        if (bundle is null || env is null || !env.IsEnabled)
        {
            StatusMessage = bundle is null ? "Backup not found." : "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(new { Q, Volume, Env });
        }

        var (ok, message, _) = await _bundleService.RestoreAsync(id, env, backupCurrentFirst: true, ct: ct);
        StatusMessage = $"{bundle.StackName}: {message}";
        IsError = !ok;
        return RedirectToPage(new { Q, Volume, Env });
    }

    public async Task<IActionResult> OnPostDeleteBundleAsync(long id)
    {
        var (found, error) = await _bundleService.DeleteBundleAsync(id, HttpContext.RequestAborted);
        StatusMessage = !found ? "Backup not found." : error ?? "Stack backup deleted.";
        IsError = !found || error is not null;
        return RedirectToPage(new { Q, Volume, Env });
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _backupService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Backup deleted." : "Backup not found.";
        IsError = !deleted;
        return RedirectToPage(new { Q, Volume, Env });
    }
}
