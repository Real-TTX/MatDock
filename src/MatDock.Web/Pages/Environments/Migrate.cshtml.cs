using System.ComponentModel.DataAnnotations;
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

    public MigrateModel(EnvironmentService environmentService, VolumeMigrationService migrationService)
    {
        _environmentService = environmentService;
        _migrationService = migrationService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public DockerEnvironment? SourceEnvironment { get; private set; }
    public List<DockerEnvironment> TargetEnvironments { get; private set; } = new();
    public VolumeMigrationResult? Result { get; private set; }

    public class InputModel
    {
        public long SourceEnvId { get; set; }

        [Required]
        public string SourceVolume { get; set; } = string.Empty;

        [Required(ErrorMessage = "Bitte ein Ziel-Environment wählen.")]
        public long TargetEnvId { get; set; }

        [Required(ErrorMessage = "Bitte einen Ziel-Volume-Namen angeben.")]
        public string TargetVolume { get; set; } = string.Empty;

        public bool Overwrite { get; set; }
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

        var request = new VolumeMigrationRequest
        {
            Source = _environmentService.BuildSettings(SourceEnvironment),
            SourceVolume = Input.SourceVolume,
            Target = _environmentService.BuildSettings(target),
            TargetVolume = Input.TargetVolume.Trim(),
            Overwrite = Input.Overwrite
        };

        Result = await _migrationService.MigrateAsync(request, HttpContext.RequestAborted);
        return Page();
    }

    private async Task LoadAsync()
    {
        SourceEnvironment = await _environmentService.GetAsync(Input.SourceEnvId, HttpContext.RequestAborted);
        var all = await _environmentService.GetAllAsync(HttpContext.RequestAborted);
        TargetEnvironments = all.Where(e => e.Id != Input.SourceEnvId).ToList();
    }
}
