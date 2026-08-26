using System.ComponentModel.DataAnnotations;
using MatDock.Core.Backups;
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

    public MigrateModel(EnvironmentService environmentService, VolumeMigrationService migrationService, BackupTargetService backupTargetService)
    {
        _environmentService = environmentService;
        _migrationService = migrationService;
        _backupTargetService = backupTargetService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public DockerEnvironment? SourceEnvironment { get; private set; }
    public List<DockerEnvironment> TargetEnvironments { get; private set; } = new();
    public List<BackupTarget> BackupTargets { get; private set; } = new();
    public VolumeMigrationResult? Result { get; private set; }

    public class InputModel
    {
        public long SourceEnvId { get; set; }

        [Required]
        public string SourceVolume { get; set; } = string.Empty;

        [Range(1, long.MaxValue, ErrorMessage = "Bitte ein Ziel-Environment wählen.")]
        public long TargetEnvId { get; set; }

        [Required(ErrorMessage = "Bitte einen Ziel-Volume-Namen angeben.")]
        public string TargetVolume { get; set; } = string.Empty;

        public bool Overwrite { get; set; }

        /// <summary>Direct streaming, or via a safety-net backup.</summary>
        public MigrationMode Mode { get; set; } = MigrationMode.Direct;

        /// <summary>0 = local MatDock storage; otherwise a configured backup target. Used only for ViaBackup.</summary>
        public long BackupTargetId { get; set; }

        /// <summary>Keep the intermediate backup after a successful ViaBackup migration.</summary>
        public bool KeepBackup { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(long sourceId, string volume)
    {
        var source = await _environmentService.GetAsync(sourceId, HttpContext.RequestAborted);
        if (source is null)
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
        if (SourceEnvironment is null)
        {
            return NotFound();
        }

        if (!VolumeCommands.IsValidVolumeName(Input.TargetVolume))
        {
            ModelState.AddModelError("Input.TargetVolume", "Nur Buchstaben, Zahlen und . _ - erlaubt (Beginn alphanumerisch).");
        }

        if (Input.TargetEnvId == Input.SourceEnvId)
        {
            ModelState.AddModelError("Input.TargetEnvId", "Quelle und Ziel müssen unterschiedlich sein.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var target = await _environmentService.GetAsync(Input.TargetEnvId, HttpContext.RequestAborted);
        if (target is null)
        {
            ModelState.AddModelError("Input.TargetEnvId", "Ziel-Environment nicht gefunden.");
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
                    ModelState.AddModelError("Input.BackupTargetId", "Backup-Ziel nicht gefunden.");
                    return Page();
                }
            }

            Result = await _migrationService.MigrateViaBackupAsync(
                SourceEnvironment, Input.SourceVolume, target, targetVolume,
                Input.Overwrite, backupTarget, Input.KeepBackup, HttpContext.RequestAborted);
            return Page();
        }

        var request = new VolumeMigrationRequest
        {
            Source = _environmentService.BuildSettings(SourceEnvironment),
            SourceVolume = Input.SourceVolume,
            Target = _environmentService.BuildSettings(target),
            TargetVolume = targetVolume,
            Overwrite = Input.Overwrite
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
    }
}
