using System.ComponentModel.DataAnnotations;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Backups;

public class RestoreModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly VolumeBackupService _backupService;

    public RestoreModel(EnvironmentService environmentService, VolumeBackupService backupService)
    {
        _environmentService = environmentService;
        _backupService = backupService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public VolumeBackup? Backup { get; private set; }
    public List<DockerEnvironment> Environments { get; private set; } = new();
    public RestoreResult? Result { get; private set; }

    public class InputModel
    {
        public long BackupId { get; set; }

        [Range(1, long.MaxValue, ErrorMessage = "Bitte ein Ziel-Environment wählen.")]
        public long TargetEnvId { get; set; }

        [Required(ErrorMessage = "Bitte einen Ziel-Volume-Namen angeben.")]
        public string TargetVolume { get; set; } = string.Empty;

        public bool Overwrite { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        Backup = await _backupService.GetAsync(id, HttpContext.RequestAborted);
        if (Backup is null)
        {
            return NotFound();
        }

        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        Input.BackupId = id;
        Input.TargetVolume = Backup.VolumeName;
        Input.TargetEnvId = Environments.FirstOrDefault(e => e.Id == Backup.SourceEnvironmentId)?.Id
                            ?? Environments.FirstOrDefault()?.Id ?? 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Backup = await _backupService.GetAsync(Input.BackupId, HttpContext.RequestAborted);
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        if (Backup is null)
        {
            return NotFound();
        }

        if (!VolumeCommands.IsValidVolumeName(Input.TargetVolume))
        {
            ModelState.AddModelError("Input.TargetVolume", "Nur Buchstaben, Zahlen und . _ - erlaubt (Beginn alphanumerisch).");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var target = await _environmentService.GetAsync(Input.TargetEnvId, HttpContext.RequestAborted);
        if (target is null || !target.IsEnabled)
        {
            ModelState.AddModelError("Input.TargetEnvId", "Ziel-Environment nicht verfügbar (deaktiviert oder gelöscht).");
            return Page();
        }

        Result = await _backupService.RestoreAsync(
            Input.BackupId, target, Input.TargetVolume.Trim(), Input.Overwrite, HttpContext.RequestAborted);
        return Page();
    }
}
