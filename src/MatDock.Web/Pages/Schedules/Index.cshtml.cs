using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Schedules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Schedules;

public class IndexModel : PageModel
{
    private readonly ScheduleService _service;
    private readonly EnvironmentService _environments;

    public IndexModel(ScheduleService service, EnvironmentService environments)
    {
        _service = service;
        _environments = environments;
    }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<ScheduledTask> Tasks { get; private set; } = new();
    public Dictionary<long, string> EnvNames { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Tasks = await _service.GetAllAsync(HttpContext.RequestAborted);
        EnvNames = (await _environments.GetAllAsync(HttpContext.RequestAborted)).ToDictionary(e => e.Id, e => e.Name);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        await _service.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = "Schedule deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunNowAsync(long id)
    {
        var summary = await _service.RunNowAsync(id, HttpContext.RequestAborted);
        StatusMessage = summary;
        return RedirectToPage();
    }
}
