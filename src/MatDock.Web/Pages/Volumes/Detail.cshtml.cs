using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Volumes;

public class DetailModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;
    private readonly ContainerService _containerService;
    private readonly MatDock.Core.Volumes.VolumeBackupService _backups;

    public DetailModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService, ContainerService containerService,
        MatDock.Core.Volumes.VolumeBackupService backups)
    {
        _backups = backups;
        _environmentService = environmentService;
        _connectionService = connectionService;
        _containerService = containerService;
    }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public long EnvId { get; private set; }
    public string VolumeName { get; private set; } = string.Empty;
    public DockerEnvironment? Environment { get; private set; }
    public DockerVolumeDetail? Detail { get; private set; }
    public IReadOnlyList<DockerContainer> UsedBy { get; private set; } = new List<DockerContainer>();
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(long envId, string volume)
    {
        EnvId = envId;
        VolumeName = volume ?? string.Empty;

        Environment = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (Environment is null || !Environment.IsEnabled)
        {
            return NotFound();
        }

        try
        {
            Detail = await _connectionService.InspectVolumeAsync(
                _environmentService.BuildSettings(Environment), VolumeName, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }

        // Which containers mount this volume (best-effort; empty on failure).
        UsedBy = await _containerService.ListByVolumeAsync(Environment, VolumeName, HttpContext.RequestAborted);
        return Page();
    }

    /// <summary>Backs the volume up now into the local backup store (/data/backups); the containers keep running unless asked.</summary>
    public async Task<IActionResult> OnPostBackupAsync(long envId, string volume, bool stopContainers)
    {
        if (!User.IsInRole(nameof(UserRole.Admin)))
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            return NotFound();
        }

        var result = await _backups.BackupAsync(env, volume ?? string.Empty, target: null, ct: HttpContext.RequestAborted, stopContainers: stopContainers);
        StatusMessage = result.Message;
        IsError = !result.Success;
        return RedirectToPage(new { envId, volume });
    }

    public async Task<IActionResult> OnPostRemoveAsync(long envId, string volume)
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
            return RedirectToPage("/Volumes/Index", new { EnvId = envId });
        }

        // Guard: never delete a volume that a container still mounts (docker would refuse anyway).
        var usedBy = await _containerService.ListByVolumeAsync(env, volume ?? string.Empty, HttpContext.RequestAborted);
        if (usedBy.Count > 0)
        {
            StatusMessage = $"Volume \"{volume}\" is in use by {usedBy.Count} container(s) and was not deleted.";
            IsError = true;
            return RedirectToPage(new { envId, volume });
        }

        var result = await _connectionService.RemoveVolumesAsync(
            _environmentService.BuildSettings(env), new[] { volume ?? string.Empty }, HttpContext.RequestAborted);
        StatusMessage = result.Success ? $"Volume \"{volume}\" deleted." : result.Detail;
        IsError = !result.Success;
        return result.Success
            ? RedirectToPage("/Volumes/Index", new { EnvId = envId, Refresh = true })
            : RedirectToPage(new { envId, volume });
    }
}
