using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Schedules;

public class IndexModel : PageModel
{
    private readonly ScheduleService _service;
    private readonly EnvironmentService _environments;
    private readonly BackupTargetService _targets;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ScheduleService service, EnvironmentService environments, BackupTargetService targets, ScheduleRuns runs,
        IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<IndexModel> logger)
    {
        _service = service;
        _environments = environments;
        _targets = targets;
        Runs = runs;
        _scopes = scopes;
        _lifetime = lifetime;
        _logger = logger;
    }

    public ScheduleRuns Runs { get; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<ScheduledTask> Tasks { get; private set; } = new();
    public Dictionary<long, string> EnvNames { get; private set; } = new();
    public Dictionary<long, string> TargetNames { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Tasks = await _service.GetAllAsync(HttpContext.RequestAborted);
        EnvNames = (await _environments.GetAllAsync(HttpContext.RequestAborted)).ToDictionary(e => e.Id, e => e.Name);
        TargetNames = (await _targets.GetAllAsync(HttpContext.RequestAborted)).ToDictionary(t => t.Id, t => t.Name);
    }

    public IActionResult OnPostRunNow(long id)
    {
        var started = ScheduleView.StartNow(_scopes, Runs, id, _lifetime.ApplicationStopping, _logger);
        StatusMessage = started ? "Started. The list shows it running." : "It runs already.";
        IsError = !started;
        return RedirectToPage();
    }

    public IActionResult OnPostCancel(long id)
    {
        var cancelled = Runs.Cancel(id);
        StatusMessage = cancelled ? "Cancelling: the run stops at its next step." : "It does not run (any more).";
        IsError = !cancelled;
        return RedirectToPage();
    }
}
