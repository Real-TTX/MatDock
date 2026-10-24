using MatDock.Core.Entities;
using MatDock.Core.Proxies;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings.Proxies;

public class IndexModel : PageModel
{
    private static readonly IReadOnlyList<SortChoice<ProxyConnection>> SortChoices =
        ListView.Sorts<ProxyConnection>(("Name", c => c.Name), ("Provider", c => c.Provider.ToString()), ("URL", c => c.Url));

    private readonly ProxyConnectionService _service;

    public IndexModel(ProxyConnectionService service) => _service = service;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? SortKey { get; set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<ProxyConnection> Connections { get; private set; } = new();
    public int Total { get; private set; }
    public ListToolbarModel Toolbar { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var all = await _service.GetAllAsync(HttpContext.RequestAborted);
        Total = all.Count;
        Connections = ListView.Apply(all, Q, SortChoices, SortKey, c => c.Name, c => c.Provider.ToString(), c => c.Url);
        Toolbar = ListView.Toolbar(Q, SortKey, "Search reverse proxies", SortChoices);
    }
}
