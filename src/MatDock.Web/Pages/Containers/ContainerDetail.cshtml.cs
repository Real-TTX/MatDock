using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Containers;

public class ContainerDetailModel : PageModel
{
    private readonly ContainerService _containerService;
    private readonly EnvironmentService _environmentService;

    public ContainerDetailModel(ContainerService containerService, EnvironmentService environmentService)
    {
        _containerService = containerService;
        _environmentService = environmentService;
    }

    public long EnvId { get; private set; }
    public string Id { get; private set; } = string.Empty;
    public DockerEnvironment? Environment { get; private set; }
    public DockerContainer? Container { get; private set; }
    public IReadOnlyList<ContainerMount> Mounts { get; private set; } = new List<ContainerMount>();
    public string? Error { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task<IActionResult> OnGetAsync(long envId, string id)
    {
        EnvId = envId;
        Id = id ?? string.Empty;
        Environment = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (Environment is not { IsEnabled: true })
        {
            return NotFound();
        }

        try
        {
            var (container, mounts) = await _containerService.GetDetailAsync(Environment, Id, HttpContext.RequestAborted);
            Container = container;
            Mounts = mounts;
            if (container is null)
            {
                Error = "Container not found.";
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostActionAsync(long envId, string id, ContainerAction action)
    {
        if (!ModelState.IsValid)
        {
            StatusMessage = "Invalid action.";
            IsError = true;
            return RedirectToPage(new { envId, id });
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(new { envId, id });
        }

        var (ok, message) = await _containerService.ActionAsync(env, id, action, HttpContext.RequestAborted);
        StatusMessage = $"{action} {id}: {message}";
        IsError = !ok;
        return RedirectToPage(new { envId, id });
    }
}
