using MatDock.Core.Entities;
using MatDock.Core.Registries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings.Registries;

public class IndexModel : PageModel
{
    private readonly RegistryService _service;

    public IndexModel(RegistryService service) => _service = service;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<ContainerRegistry> Registries { get; private set; } = new();

    public async Task OnGetAsync()
        => Registries = await _service.GetAllAsync(HttpContext.RequestAborted);
}
