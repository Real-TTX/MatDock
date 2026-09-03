using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Backups;

public class TargetsModel : PageModel
{
    private readonly BackupTargetService _targetService;
    private readonly VolumeBackupService _volumeBackupService;
    private readonly EnvironmentService _environmentService;

    public TargetsModel(BackupTargetService targetService, VolumeBackupService volumeBackupService, EnvironmentService environmentService)
    {
        _targetService = targetService;
        _volumeBackupService = volumeBackupService;
        _environmentService = environmentService;
    }

    /// <summary>Selected target (0 = local storage).</summary>
    [BindProperty(SupportsGet = true)]
    public long TargetId { get; set; }

    public List<BackupTarget> Targets { get; private set; } = new();
    public BackupTarget? Selected { get; private set; }
    public string SelectedName => Selected?.Name ?? "Local (MatDock)";
    public IReadOnlyList<BackupTargetService.TargetArchive> Archives { get; private set; } = Array.Empty<BackupTargetService.TargetArchive>();
    public List<DockerEnvironment> Environments { get; private set; } = new();
    public string? Error { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        Targets = await _targetService.GetAllAsync(HttpContext.RequestAborted);
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        Selected = TargetId > 0 ? Targets.FirstOrDefault(t => t.Id == TargetId) : null;
        if (TargetId > 0 && Selected is null)
        {
            TargetId = 0; // stale selection → local
        }

        var (archives, error) = await _targetService.ListArchivesAsync(Selected, HttpContext.RequestAborted);
        Archives = archives;
        Error = error;
    }

    public async Task<IActionResult> OnPostRestoreAsync(long targetId, string fileName, long envId, string targetVolume, bool overwrite, bool stopContainers)
    {
        var storageTarget = targetId > 0 ? await _targetService.GetAsync(targetId, HttpContext.RequestAborted) : null;
        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Target environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(new { targetId });
        }

        var result = await _volumeBackupService.RestoreFromFileAsync(storageTarget, fileName, env, targetVolume, overwrite, stopContainers, HttpContext.RequestAborted);
        StatusMessage = $"{fileName} → {env.Name}/{targetVolume}: {result.Message}";
        IsError = !result.Success;
        return RedirectToPage(new { targetId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(long targetId, string fileName)
    {
        var storageTarget = targetId > 0 ? await _targetService.GetAsync(targetId, HttpContext.RequestAborted) : null;
        await _targetService.DeleteArchiveAsync(storageTarget, fileName, HttpContext.RequestAborted);
        StatusMessage = $"Deleted {fileName}.";
        IsError = false;
        return RedirectToPage(new { targetId });
    }

    public async Task<IActionResult> OnGetDownloadAsync(long targetId, string fileName)
    {
        var storageTarget = targetId > 0 ? await _targetService.GetAsync(targetId, HttpContext.RequestAborted) : null;
        var safe = System.IO.Path.GetFileName(fileName);
        Response.ContentType = "application/octet-stream";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{safe}\"";
        try
        {
            await using var source = await _targetService.OpenArchiveAsync(storageTarget, safe, HttpContext.RequestAborted);
            await source.CopyToAsync(Response.Body, HttpContext.RequestAborted);
        }
        catch
        {
            // headers may already be sent; nothing more we can do but end the response.
        }

        return new EmptyResult();
    }
}
