using MatDock.Core.Entities;
using MatDock.Core.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks.SyncJobs;

public class IndexModel : PageModel
{
    private readonly SyncJobService _syncJobs;

    public IndexModel(SyncJobService syncJobs) => _syncJobs = syncJobs;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<SyncJob> Jobs { get; private set; } = new();
    public Dictionary<long, int> Counts { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Jobs = await _syncJobs.GetAllAsync(HttpContext.RequestAborted);
        Counts = await _syncJobs.GetItemCountsAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostRunAsync(long id)
    {
        var summary = await _syncJobs.RunNowAsync(id, force: true, HttpContext.RequestAborted);
        StatusMessage = $"Sync: {summary}";
        IsError = summary.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                  || summary.Contains("failed", StringComparison.OrdinalIgnoreCase);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        await _syncJobs.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = "Sync job deleted.";
        return RedirectToPage();
    }
}
