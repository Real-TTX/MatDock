using MatDock.Core.Entities;
using MatDock.Core.Registries;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings.Registries;

public class IndexModel : PageModel
{
    private static readonly IReadOnlyList<SortChoice<ContainerRegistry>> SortChoices =
        ListView.Sorts<ContainerRegistry>(("Name", r => r.Name), ("Host", r => r.Host), ("User", r => r.Username));

    private readonly RegistryService _service;

    public IndexModel(RegistryService service) => _service = service;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? SortKey { get; set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<ContainerRegistry> Registries { get; private set; } = new();
    public int Total { get; private set; }
    public ListToolbarModel Toolbar { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var all = await _service.GetAllAsync(HttpContext.RequestAborted);
        Total = all.Count;
        Registries = ListView.Apply(all, Q, SortChoices, SortKey, r => r.Name, r => r.Host, r => r.Username);
        Toolbar = ListView.Toolbar(Q, SortKey, "Search registries", SortChoices);
    }
}
