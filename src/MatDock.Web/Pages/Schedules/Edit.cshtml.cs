using System.ComponentModel.DataAnnotations;
using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;
using MatDock.Core.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Schedules;

public class EditModel : PageModel
{
    private readonly ScheduleService _service;
    private readonly EnvironmentService _environments;
    private readonly BackupScheduleService _backups;
    private readonly SyncJobService _syncJobs;

    public EditModel(ScheduleService service, EnvironmentService environments, BackupScheduleService backups, SyncJobService syncJobs)
    {
        _service = service;
        _environments = environments;
        _backups = backups;
        _syncJobs = syncJobs;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<BackupSchedule> BackupSchedules { get; private set; } = new();
    public List<SyncJob> SyncJobs { get; private set; } = new();
    public bool IsEdit => Input.Id is > 0;

    [TempData] public string? StatusMessage { get; set; }

    public sealed class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Name is required.")]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;
        public ScheduleTrigger Trigger { get; set; } = ScheduleTrigger.Cron;
        public string? Cron { get; set; } = "0 4 * * *";
        public ScheduleEvent Event { get; set; } = ScheduleEvent.DeployFailed;
        public ScheduleAction Action { get; set; } = ScheduleAction.Summary;
        public long? EnvironmentId { get; set; }
        public bool OptionAll { get; set; }
        public bool OptionIncludeShares { get; set; }
        public long? BackupScheduleId { get; set; }
        public long? SyncJobId { get; set; }
        public bool NotifyOnResult { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        await LoadAsync();

        if (id is > 0)
        {
            var t = await _service.GetAsync(id.Value, HttpContext.RequestAborted);
            if (t is null) { return RedirectToPage("Index"); }

            var opt = ScheduleOptions.Parse(t.OptionsJson);
            Input = new InputModel
            {
                Id = t.Id,
                Name = t.Name,
                Enabled = t.Enabled,
                Trigger = t.Trigger,
                Cron = t.Cron ?? "0 4 * * *",
                Event = t.Event,
                Action = t.Action,
                EnvironmentId = t.EnvironmentId,
                OptionAll = opt.All,
                OptionIncludeShares = opt.IncludeShares,
                BackupScheduleId = opt.BackupScheduleId,
                SyncJobId = opt.SyncJobId,
                NotifyOnResult = t.NotifyOnResult,
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadAsync();
        if (!ModelState.IsValid) { return Page(); }

        var input = ToInput();
        if (IsEdit)
        {
            var (ok, message) = await _service.UpdateAsync(Input.Id!.Value, input, HttpContext.RequestAborted);
            if (!ok) { ModelState.AddModelError(string.Empty, message); return Page(); }
        }
        else
        {
            var (ok, message, _) = await _service.CreateAsync(input, HttpContext.RequestAborted);
            if (!ok) { ModelState.AddModelError(string.Empty, message); return Page(); }
        }

        StatusMessage = "Schedule saved.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostRunNowAsync()
    {
        if (Input.Id is > 0)
        {
            StatusMessage = await _service.RunNowAsync(Input.Id.Value, HttpContext.RequestAborted);
        }
        return RedirectToPage("Index");
    }

    private async Task LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        Environments = await _environments.GetAllAsync(ct);
        BackupSchedules = await _backups.GetAllAsync(ct);
        SyncJobs = await _syncJobs.GetAllAsync(ct);
    }

    private ScheduleInput ToInput() => new()
    {
        Id = Input.Id ?? 0,
        Name = Input.Name,
        Enabled = Input.Enabled,
        Trigger = Input.Trigger,
        Cron = Input.Cron,
        Event = Input.Event,
        Action = Input.Action,
        EnvironmentId = Input.EnvironmentId,
        OptionAll = Input.OptionAll,
        OptionIncludeShares = Input.OptionIncludeShares,
        BackupScheduleId = Input.BackupScheduleId,
        SyncJobId = Input.SyncJobId,
        NotifyOnResult = Input.NotifyOnResult,
    };
}
