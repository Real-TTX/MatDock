using System.ComponentModel.DataAnnotations;
using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Schedules;

public class EditModel : PageModel
{
    private readonly ScheduleService _service;
    private readonly EnvironmentService _environments;
    private readonly BackupTargetService _backupTargets;

    public EditModel(ScheduleService service, EnvironmentService environments, BackupTargetService backupTargets)
    {
        _service = service;
        _environments = environments;
        _backupTargets = backupTargets;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<BackupTarget> BackupTargets { get; private set; } = new();
    public bool IsEdit => Input.Id is > 0;

    /// <summary>Owner of a schedule created through the API (shown as a hint; a save here keeps it).</summary>
    public string? ManagedBy { get; private set; }
    /// <summary>A copy of a sync job's timing: it belongs to the job and cannot be deleted here.</summary>
    public bool IsSyncSchedule { get; private set; }

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
        public string? VolumesCsv { get; set; }
        public long? BackupTargetId { get; set; }
        public int RetentionCount { get; set; }
        public int RetentionDays { get; set; }
        public bool StopContainers { get; set; }
        public bool NotifyOnResult { get; set; }
        public string? BundleStack { get; set; }
        public bool UpdateApply { get; set; }
        public bool UpdateBackupFirst { get; set; }
        public bool UpdateRollback { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        await LoadAsync();

        if (id is > 0)
        {
            var t = await _service.GetAsync(id.Value, HttpContext.RequestAborted);
            if (t is null) { return RedirectToPage("Index"); }

            // Sync schedules are managed in the Sync editor (single source of truth for their timing).
            if (t.SourceKind == ScheduleService.SyncSource && t.SourceId is { } sid)
            {
                return Redirect($"/Stacks/SyncJobs/Edit?id={sid}");
            }

            var opt = ScheduleOptions.Parse(t.OptionsJson);
            ManagedBy = t.ManagedBy;
            IsSyncSchedule = t.SourceKind == ScheduleService.SyncSource;
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
                VolumesCsv = opt.VolumesCsv,
                BackupTargetId = opt.BackupTargetId,
                RetentionCount = opt.RetentionCount,
                RetentionDays = opt.RetentionDays,
                StopContainers = opt.StopContainers,
                NotifyOnResult = t.NotifyOnResult,
                BundleStack = opt.BundleStack,
                UpdateApply = opt.UpdateApply,
                UpdateBackupFirst = opt.UpdateBackupFirst,
                UpdateRollback = opt.UpdateRollback,
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
        BackupTargets = await _backupTargets.GetAllAsync(ct);
        if (Input.Id is > 0)
        {
            var task = await _service.GetAsync(Input.Id.Value, ct);
            ManagedBy = task?.ManagedBy;
            IsSyncSchedule = task?.SourceKind == ScheduleService.SyncSource;
        }
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
        VolumesCsv = Input.VolumesCsv,
        BackupTargetId = Input.BackupTargetId,
        RetentionCount = Input.RetentionCount,
        RetentionDays = Input.RetentionDays,
        StopContainers = Input.StopContainers,
        NotifyOnResult = Input.NotifyOnResult,
        BundleStack = Input.BundleStack,
        UpdateApply = Input.UpdateApply,
        UpdateBackupFirst = Input.UpdateBackupFirst,
        UpdateRollback = Input.UpdateRollback,
    };

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        // The timing of a sync job belongs to the job: deleting its copy here would leave the job without a schedule.
        if ((await _service.GetAsync(id, HttpContext.RequestAborted))?.SourceKind == ScheduleService.SyncSource)
        {
            StatusMessage = "This schedule belongs to a sync job; edit the job instead.";
            TempData["IsError"] = true;
            return RedirectToPage("Index");
        }

        await _service.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = "Schedule deleted.";
        return RedirectToPage("Index");
    }
}
