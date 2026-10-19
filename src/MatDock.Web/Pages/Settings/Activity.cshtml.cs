using MatDock.Core.Activity;
using MatDock.Core.Entities;
using MatDock.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings;

/// <summary>The activity log: what was done in MatDock, by whom, across all environments.</summary>
public class ActivityModel : PageModel
{
    private const int PageSize = 50;
    private readonly ActivityLogService _activity;

    public ActivityModel(ActivityLogService activity) => _activity = activity;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public bool Failed { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public IReadOnlyList<ActivityEntry> Items { get; private set; } = Array.Empty<ActivityEntry>();
    public PaginationModel Pagination { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var (items, total) = await _activity.QueryAsync(Q, Failed, Math.Max(1, PageNumber), PageSize, HttpContext.RequestAborted);
        Pagination = new PaginationModel { Page = Math.Max(1, PageNumber), PageSize = PageSize, TotalItems = total };
        Pagination.Normalize();
        Items = items;
    }
}
