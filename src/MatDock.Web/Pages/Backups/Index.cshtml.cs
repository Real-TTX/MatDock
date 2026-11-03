using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;
using MatDock.Core.Volumes;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Backups;

public class IndexModel : PageModel
{
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly VolumeBackupService _backupService;
    private readonly BundleBackupService _bundleService;
    private readonly EnvironmentService _environments;

    private readonly ScheduleService _schedules;
    private readonly BackupTargetService _targets;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(VolumeBackupService backupService, BundleBackupService bundleService, EnvironmentService environments, ScheduleService schedules,
        BackupTargetService targets, ScheduleRuns runs, IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<IndexModel> logger)
    {
        _schedules = schedules;
        _targets = targets;
        Runs = runs;
        _scopes = scopes;
        _lifetime = lifetime;
        _logger = logger;
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

    public ScheduleRuns Runs { get; }

    /// <summary>The schedules that back something up (volumes, stacks, MatDock itself): what, where, when, how it went, and the one running now.</summary>
    public List<ScheduledTask> Jobs { get; private set; } = new();

    public Dictionary<long, string> JobEnvNames { get; private set; } = new();

    public Dictionary<long, string> TargetNames { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        Jobs = (await _schedules.GetAllAsync(HttpContext.RequestAborted))
            .Where(t => t.Action is ScheduleAction.Backup or ScheduleAction.BundleBackup or ScheduleAction.SelfBackup)
            .OrderByDescending(t => Runs.Of(t.Id) is not null).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        JobEnvNames = (await _environments.GetAllAsync(HttpContext.RequestAborted)).ToDictionary(e => e.Id, e => e.Name);
        TargetNames = (await _targets.GetAllAsync(HttpContext.RequestAborted)).ToDictionary(t => t.Id, t => t.Name);
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

    public IActionResult OnPostRunJob(long id)
    {
        var started = ScheduleView.StartNow(_scopes, Runs, id, _lifetime.ApplicationStopping, _logger);
        StatusMessage = started ? "Backup started. The list shows it running." : "It runs already.";
        IsError = !started;
        return RedirectToPage();
    }

    public IActionResult OnPostCancelJob(long id)
    {
        var cancelled = Runs.Cancel(id);
        StatusMessage = cancelled ? "Cancelling: the backup stops at its next step." : "It does not run (any more).";
        IsError = !cancelled;
        return RedirectToPage();
    }
}
