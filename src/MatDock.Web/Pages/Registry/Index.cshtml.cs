using MatDock.Core.Entities;
using MatDock.Core.Registries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Registry;

/// <summary>Browses a registry's repositories (catalog) and a selected repository's tags via the v2 API.</summary>
public class IndexModel : PageModel
{
    private readonly RegistryService _service;

    public IndexModel(RegistryService service) => _service = service;

    [BindProperty(SupportsGet = true)] public long? ConnId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Repo { get; set; }

    public List<ContainerRegistry> Registries { get; private set; } = new();
    public ContainerRegistry? Selected { get; private set; }
    public RegistryCatalog? Catalog { get; private set; }
    public RegistryTagList? Tags { get; private set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Registries = await _service.GetAllAsync(ct);
        Selected = ConnId is > 0
            ? Registries.FirstOrDefault(r => r.Id == ConnId)
            : Registries.FirstOrDefault();
        ConnId = Selected?.Id;

        if (Selected is null)
        {
            return;
        }

        Catalog = await _service.ListRepositoriesAsync(Selected.Id, ct);

        var repo = Repo?.Trim();
        if (!string.IsNullOrEmpty(repo))
        {
            Tags = await _service.ListTagsAsync(Selected.Id, repo, ct);
        }
    }
}
