using MatDock.Core.Backups;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
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
    private readonly IMemoryCache _cache;

    public IndexModel(
        EnvironmentService environmentService,
        IEnvironmentConnectionService connectionService,
        VolumeBackupService backupService,
        BackupTargetService backupTargetService,
        IMemoryCache cache)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
        _backupService = backupService;
        _backupTargetService = backupTargetService;
        _cache = cache;
    }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public long EnvId { get; set; }

    /// <summary>Force a fresh query, bypassing the short-lived cache (the "Aktualisieren" button).</summary>
    [BindProperty(SupportsGet = true)]
    public bool Refresh { get; set; }

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<VolumeRow> Rows { get; private set; } = new();
    public List<(string Environment, string Message)> Errors { get; private set; } = new();

    public sealed record VolumeRow(DockerEnvironment Environment, DockerVolume Volume);

    private sealed record CachedVolumes(IReadOnlyList<DockerVolume> Volumes, string? Error);

    public async Task OnGetAsync()
    {
        Environments = await _environmentService.GetAllAsync(HttpContext.RequestAborted);

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

    public async Task<IActionResult> OnPostBackupAsync(long environmentId, string volume)
    {
        var env = await _environmentService.GetAsync(environmentId, HttpContext.RequestAborted);
        if (env is null)
        {
            StatusMessage = "Environment nicht gefunden.";
            IsError = true;
            return RedirectToPage(new { EnvId, Q });
        }

        var target = await _backupTargetService.GetDefaultAsync(HttpContext.RequestAborted);
        var result = await _backupService.BackupAsync(env, volume, target, scheduleId: null, HttpContext.RequestAborted);
        var where = target is null ? "Lokal" : target.Name;
        StatusMessage = $"{volume} → {where}: {result.Message}";
        IsError = !result.Success;
        return RedirectToPage(new { EnvId, Q });
    }
}
