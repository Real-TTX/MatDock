using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Pages.Environments;

public class IndexModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly EnvironmentService _environmentService;
    private readonly MatDock.Web.Support.HostReadings _readings;

    public IndexModel(EnvironmentService environmentService, MatDock.Web.Support.HostReadings readings)
    {
        _environmentService = environmentService;
        _readings = readings;
    }

    [BindProperty(SupportsGet = true)]
    public string View { get; set; } = "list";

    public Dictionary<long, HostStats> Stats { get; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string SortKey { get; set; } = "NameAsc";

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public List<DockerEnvironment> Items { get; private set; } = new();
    public PaginationModel Pagination { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        var all = await _environmentService.GetAllAsync(HttpContext.RequestAborted);
        IEnumerable<DockerEnvironment> query = all;

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            query = query.Where(e =>
                e.Name.Contains(s, Ic) ||
                e.Host.Contains(s, Ic) ||
                e.Username.Contains(s, Ic) ||
                (e.Description ?? string.Empty).Contains(s, Ic));
        }

        if (!string.IsNullOrWhiteSpace(Status) && Enum.TryParse<EnvironmentStatus>(Status, out var statusFilter))
        {
            query = query.Where(e => e.Status == statusFilter);
        }

        var descending = SortKey.EndsWith("Desc", Ic);
        query = SortKey switch
        {
            "HostAsc" or "HostDesc" => Order(query, e => e.Host, descending),
            "StatusAsc" or "StatusDesc" => Order(query, e => (int)e.Status, descending),
            "CheckedAsc" or "CheckedDesc" => Order(query, e => e.LastCheckedAt ?? DateTime.MinValue, descending),
            _ => Order(query, e => e.Name, descending)
        };

        var list = query.ToList();
        Pagination = new PaginationModel { Page = PageNumber, PageSize = PageSize, TotalItems = list.Count };
        Pagination.Normalize();
        Items = list
            .Skip((Pagination.Page - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        // Both views show the same info now, so load host stats for either (cached, disabled envs skipped).
        await LoadStatsAsync(Items);
    }

    private async Task LoadStatsAsync(IReadOnlyList<DockerEnvironment> environments)
    {
        // Never open SSH to a deactivated host — its card renders with muted gauges instead. The last reading is shown while a new one is taken.
        var tasks = environments.Where(e => e.IsEnabled).Select(async env => (env.Id, await _readings.StatsAsync(env, HttpContext.RequestAborted)));
        foreach (var (id, stats) in await Task.WhenAll(tasks))
        {
            Stats[id] = stats;
        }
    }

    private static IEnumerable<DockerEnvironment> Order<TKey>(IEnumerable<DockerEnvironment> source, Func<DockerEnvironment, TKey> key, bool descending)
        => descending ? source.OrderByDescending(key) : source.OrderBy(key);
}
