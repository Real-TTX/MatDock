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
    private static readonly TimeSpan StatsTtl = TimeSpan.FromSeconds(30);

    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;
    private readonly IMemoryCache _cache;

    public IndexModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService, IMemoryCache cache)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
        _cache = cache;
    }

    [BindProperty(SupportsGet = true)]
    public string View { get; set; } = "list";

    public Dictionary<long, HostStats> Stats { get; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name";

    [BindProperty(SupportsGet = true)]
    public string Dir { get; set; } = "asc";

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

        var descending = string.Equals(Dir, "desc", Ic);
        query = Sort.ToLowerInvariant() switch
        {
            "host" => Order(query, e => e.Host, descending),
            "status" => Order(query, e => (int)e.Status, descending),
            "checked" => Order(query, e => e.LastCheckedAt ?? DateTime.MinValue, descending),
            _ => Order(query, e => e.Name, descending)
        };

        var list = query.ToList();
        Pagination = new PaginationModel { Page = PageNumber, PageSize = PageSize, TotalItems = list.Count };
        Pagination.Normalize();
        Items = list
            .Skip((Pagination.Page - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        if (string.Equals(View, "gallery", Ic))
        {
            await LoadStatsAsync(Items);
        }
    }

    private async Task LoadStatsAsync(IReadOnlyList<DockerEnvironment> environments)
    {
        using var gate = new SemaphoreSlim(4);
        // Never open SSH to a deactivated host — its card renders with muted gauges instead.
        var tasks = environments.Where(e => e.IsEnabled).Select(async env =>
        {
            var cacheKey = $"hoststats:{env.Id}";
            if (_cache.TryGetValue(cacheKey, out HostStats? cached) && cached is not null)
            {
                return (env.Id, cached);
            }

            await gate.WaitAsync(HttpContext.RequestAborted);
            try
            {
                HostStats stats;
                try
                {
                    stats = await _connectionService.GetHostStatsAsync(_environmentService.BuildSettings(env), HttpContext.RequestAborted);
                }
                catch
                {
                    stats = new HostStats(); // unreachable → empty gauges
                }

                _cache.Set(cacheKey, stats, StatsTtl);
                return (env.Id, stats);
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var (id, stats) in await Task.WhenAll(tasks))
        {
            Stats[id] = stats;
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _environmentService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Environment gelöscht." : "Environment nicht gefunden.";
        IsError = !deleted;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(long id)
    {
        var result = await _environmentService.TestAndPersistAsync(id, HttpContext.RequestAborted);
        StatusMessage = result.Message;
        IsError = !result.Success;
        return RedirectToPage(new { View });
    }

    public async Task<IActionResult> OnPostToggleAsync(long id, bool enable)
    {
        var ok = await _environmentService.SetEnabledAsync(id, enable, HttpContext.RequestAborted);
        StatusMessage = ok
            ? (enable ? "Environment aktiviert." : "Environment deaktiviert – wird von automatischen Läufen und Auswahllisten ausgeschlossen.")
            : "Environment nicht gefunden.";
        IsError = !ok;
        return RedirectToPage(new { View });
    }

    private static IEnumerable<DockerEnvironment> Order<TKey>(IEnumerable<DockerEnvironment> source, Func<DockerEnvironment, TKey> key, bool descending)
        => descending ? source.OrderByDescending(key) : source.OrderBy(key);
}
