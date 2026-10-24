using MatDock.Core.Entities;
using MatDock.Core.Sync;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks.SyncJobs;

public class IndexModel : PageModel
{
    private static readonly IReadOnlyList<SortChoice<SyncJob>> SortChoices =
        ListView.Sorts<SyncJob>(("Name", j => j.Name), ("Repository", j => j.GitRepoUrl), ("Last run", j => j.LastRunAt));

    private readonly SyncJobService _syncJobs;

    public IndexModel(SyncJobService syncJobs) => _syncJobs = syncJobs;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? SortKey { get; set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<SyncJob> Jobs { get; private set; } = new();
    public int Total { get; private set; }
    public ListToolbarModel Toolbar { get; private set; } = null!;
    public Dictionary<long, int> Counts { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var all = await _syncJobs.GetAllAsync(HttpContext.RequestAborted);
        Total = all.Count;
        Jobs = ListView.Apply(all, Q, SortChoices, SortKey, j => j.Name, j => j.GitRepoUrl, j => j.GitReference);
        Toolbar = ListView.Toolbar(Q, SortKey, "Search sync jobs", SortChoices);
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
}
