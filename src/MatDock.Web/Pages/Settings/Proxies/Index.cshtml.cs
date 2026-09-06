using MatDock.Core.Entities;
using MatDock.Core.Proxies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings.Proxies;

public class IndexModel : PageModel
{
    private readonly ProxyConnectionService _service;

    public IndexModel(ProxyConnectionService service) => _service = service;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public List<ProxyConnection> Connections { get; private set; } = new();

    public async Task OnGetAsync()
        => Connections = await _service.GetAllAsync(HttpContext.RequestAborted);

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        await _service.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = "Proxy connection deleted.";
        return RedirectToPage();
    }
}
