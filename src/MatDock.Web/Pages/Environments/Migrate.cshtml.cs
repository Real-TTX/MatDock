using System.ComponentModel.DataAnnotations;
using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Environments;

public class MigrateModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly VolumeMigrationService _migrationService;
    private readonly BackupTargetService _backupTargetService;
    private readonly ContainerService _containerService;

    public MigrateModel(EnvironmentService environmentService, VolumeMigrationService migrationService, BackupTargetService backupTargetService, ContainerService containerService)
    {
        _environmentService = environmentService;
        _migrationService = migrationService;
        _backupTargetService = backupTargetService;
        _containerService = containerService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public DockerEnvironment? SourceEnvironment { get; private set; }
    public List<DockerEnvironment> TargetEnvironments { get; private set; } = new();
    public List<BackupTarget> BackupTargets { get; private set; } = new();
    public IReadOnlyList<DockerContainer> AffectedContainers { get; private set; } = new List<DockerContainer>();
    public VolumeMigrationResult? Result { get; private set; }

    public class InputModel
    {
        public long SourceEnvId { get; set; }

        [Required]
        public string SourceVolume { get; set; } = string.Empty;

        [Range(1, long.MaxValue, ErrorMessage = "Please choose a target environment.")]
        public long TargetEnvId { get; set; }

        [Required(ErrorMessage = "Please enter a target volume name.")]
        public string TargetVolume { get; set; } = string.Empty;

        public bool Overwrite { get; set; }

        /// <summary>Direct streaming, or via a safety-net backup.</summary>
        public MigrationMode Mode { get; set; } = MigrationMode.Direct;

        /// <summary>0 = local MatDock storage; otherwise a configured backup target. Used only for ViaBackup.</summary>
        public long BackupTargetId { get; set; }

        /// <summary>Keep the intermediate backup after a successful ViaBackup migration.</summary>
        public bool KeepBackup { get; set; } = true;

        /// <summary>Stop the source volume's containers during the transfer, then restart them.</summary>
        public bool StopContainers { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long sourceId, string volume)
    {
        var source = await _environmentService.GetAsync(sourceId, HttpContext.RequestAborted);
        if (source is null || !source.IsEnabled)
        {
            return NotFound();
        }

        Input.SourceEnvId = sourceId;
        Input.SourceVolume = volume;
        Input.TargetVolume = volume;
        Input.KeepBackup = true;

        await LoadAsync();
        Input.TargetEnvId = TargetEnvironments.FirstOrDefault()?.Id ?? 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (SourceEnvironment is null || !SourceEnvironment.IsEnabled)
        {
            return NotFound();
        }

        if (!VolumeCommands.IsValidVolumeName(Input.TargetVolume))
        {
            ModelState.AddModelError("Input.TargetVolume", "Only letters, numbers and . _ - are allowed (must start alphanumeric).");
        }

        if (Input.TargetEnvId == Input.SourceEnvId)
        {
            ModelState.AddModelError("Input.TargetEnvId", "Source and target must be different.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var target = await _environmentService.GetAsync(Input.TargetEnvId, HttpContext.RequestAborted);
        if (target is null || !target.IsEnabled)
        {
            ModelState.AddModelError("Input.TargetEnvId", "Target environment not available (disabled or deleted).");
            return Page();
        }

        var targetVolume = Input.TargetVolume.Trim();
        if (Input.Mode == MigrationMode.ViaBackup)
        {
            BackupTarget? backupTarget = null;
            if (Input.BackupTargetId > 0)
            {
                backupTarget = await _backupTargetService.GetAsync(Input.BackupTargetId, HttpContext.RequestAborted);
                if (backupTarget is null)
                {
                    ModelState.AddModelError("Input.BackupTargetId", "Backup target not found.");
                    return Page();
                }
            }

            Result = await _migrationService.MigrateViaBackupAsync(
                SourceEnvironment, Input.SourceVolume, target, targetVolume,
                Input.Overwrite, backupTarget, Input.KeepBackup, Input.StopContainers, HttpContext.RequestAborted);
            return Page();
        }

        var request = new VolumeMigrationRequest
        {
            Source = _environmentService.BuildSettings(SourceEnvironment),
            SourceVolume = Input.SourceVolume,
            Target = _environmentService.BuildSettings(target),
            TargetVolume = targetVolume,
            Overwrite = Input.Overwrite,
            StopContainers = Input.StopContainers
        };

        Result = await _migrationService.MigrateAsync(request, HttpContext.RequestAborted);
        return Page();
    }

    private async Task LoadAsync()
    {
        SourceEnvironment = await _environmentService.GetAsync(Input.SourceEnvId, HttpContext.RequestAborted);
        // Only active environments are valid migration targets.
        var enabled = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        TargetEnvironments = enabled.Where(e => e.Id != Input.SourceEnvId).ToList();
        BackupTargets = await _backupTargetService.GetAllAsync(HttpContext.RequestAborted);

        // Show which containers on the source use this volume (for the "stop during transfer" option).
        if (SourceEnvironment is { IsEnabled: true } && VolumeCommands.IsValidVolumeName(Input.SourceVolume))
        {
            AffectedContainers = await _containerService.ListByVolumeAsync(SourceEnvironment, Input.SourceVolume, HttpContext.RequestAborted);
        }
    }
}
