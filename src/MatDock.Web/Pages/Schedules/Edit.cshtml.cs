using System.ComponentModel.DataAnnotations;
using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Schedules;

public class EditModel : PageModel
{
    private readonly BackupScheduleService _scheduleService;
    private readonly EnvironmentService _environmentService;
    private readonly BackupTargetService _targetService;
    private readonly IEnvironmentConnectionService _connectionService;

    public EditModel(
        BackupScheduleService scheduleService,
        EnvironmentService environmentService,
        BackupTargetService targetService,
        IEnvironmentConnectionService connectionService)
    {
        _scheduleService = scheduleService;
        _environmentService = environmentService;
        _targetService = targetService;
        _connectionService = connectionService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;
    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<BackupTarget> Targets { get; private set; } = new();
    public List<string> AvailableVolumes { get; private set; } = new();
    public string? VolumeLoadError { get; private set; }

    public class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Bitte einen Namen angeben.")]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Range(1, long.MaxValue, ErrorMessage = "Bitte ein Environment wählen.")]
        public long EnvironmentId { get; set; }

        public List<string> SelectedVolumes { get; set; } = new();

        [Display(Name = "Weitere Volumes (eine pro Zeile)")]
        public string? ExtraVolumes { get; set; }

        public long? BackupTargetId { get; set; }

        [Required(ErrorMessage = "Bitte einen Cron-Ausdruck angeben.")]
        public string Cron { get; set; } = "0 3 * * *";

        [Range(0, int.MaxValue)]
        public int RetentionCount { get; set; } = 7;

        [Range(0, int.MaxValue)]
        public int RetentionDays { get; set; }

        public bool Enabled { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        await LoadListsAsync();

        if (id is > 0)
        {
            var s = await _scheduleService.GetAsync(id.Value, HttpContext.RequestAborted);
            if (s is null)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Id = s.Id,
                Name = s.Name,
                EnvironmentId = s.EnvironmentId,
                SelectedVolumes = s.Volumes.ToList(),
                BackupTargetId = s.BackupTargetId,
                Cron = s.Cron,
                RetentionCount = s.RetentionCount,
                RetentionDays = s.RetentionDays,
                Enabled = s.Enabled
            };

            await LoadVolumesAsync(s.EnvironmentId);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostLoadVolumesAsync()
    {
        await LoadListsAsync();
        await LoadVolumesAsync(Input.EnvironmentId);
        ModelState.Clear();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadListsAsync();

        if (!CronSchedule.IsValid(Input.Cron))
        {
            ModelState.AddModelError("Input.Cron", "Ungültiger Cron-Ausdruck (5 Felder, z. B. 0 3 * * *).");
        }

        var volumes = CombineVolumes();
        if (volumes.Count == 0)
        {
            ModelState.AddModelError("Input.SelectedVolumes", "Bitte mindestens ein Volume wählen oder eingeben.");
        }

        if (!ModelState.IsValid)
        {
            await LoadVolumesAsync(Input.EnvironmentId);
            return Page();
        }

        var input = new BackupScheduleInput
        {
            Name = Input.Name,
            EnvironmentId = Input.EnvironmentId,
            VolumesCsv = string.Join('\n', volumes),
            BackupTargetId = Input.BackupTargetId,
            Cron = Input.Cron,
            RetentionCount = Input.RetentionCount,
            RetentionDays = Input.RetentionDays,
            Enabled = Input.Enabled
        };

        if (IsEdit)
        {
            var ok = await _scheduleService.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            if (!ok)
            {
                return NotFound();
            }
        }
        else
        {
            await _scheduleService.CreateAsync(input, HttpContext.RequestAborted);
        }

        return RedirectToPage("/Schedules/Index");
    }

    private List<string> CombineVolumes()
    {
        var result = new List<string>(Input.SelectedVolumes);
        if (!string.IsNullOrWhiteSpace(Input.ExtraVolumes))
        {
            result.AddRange(Input.ExtraVolumes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return result.Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task LoadListsAsync()
    {
        Environments = await _environmentService.GetAllAsync(HttpContext.RequestAborted);
        Targets = await _targetService.GetAllAsync(HttpContext.RequestAborted);
    }

    private async Task LoadVolumesAsync(long environmentId)
    {
        if (environmentId <= 0)
        {
            return;
        }

        var env = Environments.FirstOrDefault(e => e.Id == environmentId);
        if (env is null)
        {
            return;
        }

        try
        {
            var volumes = await _connectionService.ListVolumesAsync(_environmentService.BuildSettings(env), HttpContext.RequestAborted);
            AvailableVolumes = volumes.Select(v => v.Name).ToList();
        }
        catch (Exception ex)
        {
            VolumeLoadError = ex.Message;
        }
    }
}
