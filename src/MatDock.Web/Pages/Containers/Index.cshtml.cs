using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Containers;

public class IndexModel : PageModel
{
    private readonly ContainerService _containerService;
    private readonly EnvironmentService _environmentService;

    public IndexModel(ContainerService containerService, EnvironmentService environmentService)
    {
        _containerService = containerService;
        _environmentService = environmentService;
    }

    [BindProperty(SupportsGet = true)]
    public long EnvId { get; set; }

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public DockerEnvironment? SelectedEnvironment { get; private set; }
    public List<DockerContainer> Containers { get; private set; } = new();
    public string? Error { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);
        if (EnvId <= 0)
        {
            return;
        }

        SelectedEnvironment = Environments.FirstOrDefault(e => e.Id == EnvId);
        if (SelectedEnvironment is null)
        {
            return;
        }

        try
        {
            Containers = (await _containerService.ListAsync(SelectedEnvironment, HttpContext.RequestAborted)).ToList();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    public async Task<IActionResult> OnPostActionAsync(long envId, string id, ContainerAction action)
    {
        // An unbindable action (crafted POST) leaves the enum at its default (Start); reject it
        // instead of silently starting the container.
        if (!ModelState.IsValid)
        {
            StatusMessage = "Ungültige Aktion.";
            IsError = true;
            return RedirectToPage(new { EnvId = envId });
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment nicht verfügbar (deaktiviert oder gelöscht).";
            IsError = true;
            return RedirectToPage(new { EnvId = envId });
        }

        var (ok, message) = await _containerService.ActionAsync(env, id, action, HttpContext.RequestAborted);
        StatusMessage = $"{action} {id}: {message}";
        IsError = !ok;
        return RedirectToPage(new { EnvId = envId });
    }
}
