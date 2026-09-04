using MatDock.Core.Backups;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Notifications;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Pages.Volumes;

public class IndexModel : PageModel
{
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;
    private readonly VolumeBackupService _backupService;
    private readonly BackupTargetService _backupTargetService;
    private readonly INotificationService _notifications;
    private readonly IMemoryCache _cache;

    public IndexModel(
        EnvironmentService environmentService,
        IEnvironmentConnectionService connectionService,
        VolumeBackupService backupService,
        BackupTargetService backupTargetService,
        INotificationService notifications,
        IMemoryCache cache)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
        _backupService = backupService;
        _backupTargetService = backupTargetService;
        _notifications = notifications;
        _cache = cache;
    }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public long EnvId { get; set; }

    /// <summary>Force a fresh query, bypassing the short-lived cache (the "Refresh" button).</summary>
    [BindProperty(SupportsGet = true)]
    public bool Refresh { get; set; }

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<VolumeRow> Rows { get; private set; } = new();
    public List<(string Environment, string Message)> Errors { get; private set; } = new();

    public sealed record VolumeRow(DockerEnvironment Environment, DockerVolume Volume);

    private sealed record CachedVolumes(IReadOnlyList<DockerVolume> Volumes, string? Error);

    public async Task OnGetAsync()
    {
        // The environment is chosen globally (sidebar dropdown / cookie); 0 = all.
        EnvId = MatDock.Web.Support.EnvSelection.Resolve(HttpContext) ?? 0;

        // Deactivated environments are excluded (not queried, not shown in the picker).
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);

        // Stale selection (env deleted or deactivated): fall back to "all" and clear the cookie.
        if (EnvId > 0 && Environments.All(e => e.Id != EnvId))
        {
            EnvId = 0;
            MatDock.Web.Support.EnvSelection.Clear(HttpContext);
        }

        var targets = (EnvId > 0 ? Environments.Where(e => e.Id == EnvId) : Environments).ToList();
        var prepared = targets.Select(e => (Env: e, Settings: _environmentService.BuildSettings(e))).ToList();

        // Limit concurrent SSH connections so opening the page never triggers an auth storm / lockout.
        using var gate = new SemaphoreSlim(4);
        var tasks = prepared.Select(async p =>
        {
            var cacheKey = $"volumes:{p.Env.Id}";
            if (!Refresh && _cache.TryGetValue(cacheKey, out CachedVolumes? cached) && cached is not null)
            {
                return (p.Env, cached);
            }

            await gate.WaitAsync(HttpContext.RequestAborted);
            try
            {
                CachedVolumes entry;
                try
                {
                    var volumes = await _connectionService.ListVolumesAsync(p.Settings, HttpContext.RequestAborted);
                    entry = new CachedVolumes(volumes, null);
                }
                catch (Exception ex)
                {
                    entry = new CachedVolumes(Array.Empty<DockerVolume>(), ex.Message);
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

        var rows = new List<VolumeRow>();
        foreach (var (env, entry) in results)
        {
            if (entry.Error is not null)
            {
                Errors.Add((env.Name, entry.Error));
                continue;
            }

            rows.AddRange(entry.Volumes.Select(v => new VolumeRow(env, v)));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            rows = rows.Where(r =>
                r.Volume.Name.Contains(s, Ic) ||
                r.Volume.Driver.Contains(s, Ic) ||
                (r.Volume.Mountpoint ?? string.Empty).Contains(s, Ic) ||
                r.Environment.Name.Contains(s, Ic)).ToList();
        }

        Rows = rows
            .OrderBy(r => r.Environment.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Volume.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IActionResult> OnPostCreateVolumeAsync(long createEnvId, string newVolumeName)
    {
        var env = await _environmentService.GetAsync(createEnvId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(new { EnvId, Q });
        }

        var (ok, message) = await _connectionService.CreateVolumeAsync(_environmentService.BuildSettings(env), newVolumeName ?? string.Empty, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        // Switch to that env and bypass the cache so the new volume shows immediately.
        return RedirectToPage(new { EnvId = createEnvId, Q, Refresh = true });
    }

    public async Task<IActionResult> OnPostBackupAsync(string single, bool stopContainers)
    {
        var (envId, volume) = VolumeSelection.Parse(new[] { single }).FirstOrDefault();
        if (volume is null)
        {
            StatusMessage = "Invalid selection.";
            IsError = true;
            return RedirectToPage(new { EnvId, Q });
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(new { EnvId, Q });
        }

        var target = await _backupTargetService.GetDefaultAsync(HttpContext.RequestAborted);
        var result = await _backupService.BackupAsync(env, volume, target, scheduleId: null, ct: HttpContext.RequestAborted, stopContainers: stopContainers);
        StatusMessage = $"{volume} → {(target?.Name ?? "Local")}: {result.Message}";
        IsError = !result.Success;
        await NotifyBackupAsync($"Manual backup: {volume}", $"{volume} → {(target?.Name ?? "Local")}: {result.Message}", result.Success);
        return RedirectToPage(new { EnvId, Q });
    }

    /// <summary>Best-effort backup notification for manual/bulk runs; never fails the request.</summary>
    private async Task NotifyBackupAsync(string title, string summary, bool success)
    {
        try
        {
            await _notifications.NotifyBackupResultAsync(title, summary, success, HttpContext.RequestAborted);
        }
        catch
        {
            // notifications are best-effort; a delivery failure must not affect the backup result page.
        }
    }
}
