using MatDock.Core.Backups;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Schedules;

public class IndexModel : PageModel
{
    private readonly BackupScheduleService _scheduleService;
    private readonly EnvironmentService _environmentService;

    public IndexModel(BackupScheduleService scheduleService, EnvironmentService environmentService)
    {
        _scheduleService = scheduleService;
        _environmentService = environmentService;
    }

    public List<BackupSchedule> Items { get; private set; } = new();
    public Dictionary<long, string> EnvironmentNames { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        Items = await _scheduleService.GetAllAsync(HttpContext.RequestAborted);
        EnvironmentNames = (await _environmentService.GetAllAsync(HttpContext.RequestAborted))
            .ToDictionary(e => e.Id, e => e.Name);
    }

    public async Task<IActionResult> OnPostRunAsync(long id)
    {
        var summary = await _scheduleService.RunNowAsync(id, HttpContext.RequestAborted);
        StatusMessage = $"Ausgeführt: {summary}";
        IsError = summary.Contains("Fehler", StringComparison.OrdinalIgnoreCase);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _scheduleService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Zeitplan gelöscht." : "Zeitplan nicht gefunden.";
        IsError = !deleted;
        return RedirectToPage();
    }
}
