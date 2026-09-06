using MatDock.Core.Entities;
using MatDock.Core.Proxies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Proxy;

/// <summary>Manages reverse-proxy routes for the selected proxy connection.</summary>
public class IndexModel : PageModel
{
    private readonly ProxyConnectionService _service;

    public IndexModel(ProxyConnectionService service) => _service = service;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    [BindProperty(SupportsGet = true)] public long? ConnId { get; set; }

    public List<ProxyConnection> Connections { get; private set; } = new();
    public ProxyConnection? Selected { get; private set; }
    public IReadOnlyList<ProxyRoute> Routes { get; private set; } = new List<ProxyRoute>();
    public string? RouteError { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();

        if (Selected is not null)
        {
            try
            {
                Routes = await _service.ListRoutesAsync(Selected.Id, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                RouteError = ex.Message;
            }
        }
    }

    public async Task<IActionResult> OnPostAddAsync(long connId, string host, string upstream, bool tls)
    {
        var result = await _service.AddRouteAsync(connId,
            new ProxyRouteInput(host ?? string.Empty, upstream ?? string.Empty, tls, null), HttpContext.RequestAborted);
        StatusMessage = result.Message;
        IsError = !result.Ok;
        return RedirectToPage(new { ConnId = connId });
    }

    public async Task<IActionResult> OnPostRemoveAsync(long connId, string routeId)
    {
        var result = await _service.RemoveRouteAsync(connId, routeId ?? string.Empty, HttpContext.RequestAborted);
        StatusMessage = result.Message;
        IsError = !result.Ok;
        return RedirectToPage(new { ConnId = connId });
    }

    private async Task LoadAsync()
    {
        Connections = await _service.GetAllAsync(HttpContext.RequestAborted);
        Selected = ConnId is > 0
            ? Connections.FirstOrDefault(c => c.Id == ConnId)
            : Connections.FirstOrDefault();
        ConnId = Selected?.Id;
    }
}
