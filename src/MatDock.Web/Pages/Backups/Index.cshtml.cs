using MatDock.Core.Entities;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Backups;

public class IndexModel : PageModel
{
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly VolumeBackupService _backupService;

    public IndexModel(VolumeBackupService backupService)
    {
        _backupService = backupService;
    }

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
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _backupService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Backup deleted." : "Backup not found.";
        IsError = !deleted;
        return RedirectToPage(new { Q, Volume, Env });
    }
}
