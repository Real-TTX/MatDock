using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Containers;

public class StackModel : PageModel
{
    private readonly ContainerService _containerService;
    private readonly EnvironmentService _environmentService;

    public StackModel(ContainerService containerService, EnvironmentService environmentService)
    {
        _containerService = containerService;
        _environmentService = environmentService;
    }

    public long EnvId { get; private set; }
    public string Project { get; private set; } = string.Empty;
    public DockerEnvironment? Environment { get; private set; }
    public IReadOnlyList<DockerContainer> Containers { get; private set; } = new List<DockerContainer>();
    public string? Error { get; private set; }

    public int RunningCount => Containers.Count(c => c.IsRunning);
    public int TotalCount => Containers.Count;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task<IActionResult> OnGetAsync(long envId, string project)
    {
        EnvId = envId;
        Project = project ?? string.Empty;
        Environment = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (Environment is not { IsEnabled: true })
        {
            return NotFound();
        }

        try
        {
            Containers = await _containerService.ListByProjectAsync(Environment, Project, HttpContext.RequestAborted);
            if (Containers.Count == 0)
            {
                Error = "Stack not found or empty.";
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostActionAsync(long envId, string project, string id, ContainerAction action)
    {
        if (!ModelState.IsValid)
        {
            StatusMessage = "Invalid action.";
            IsError = true;
            return RedirectToPage(new { envId, project });
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(new { envId, project });
        }

        var (ok, message) = await _containerService.ActionAsync(env, id, action, HttpContext.RequestAborted);
        StatusMessage = $"{action} {id}: {message}";
        IsError = !ok;
        return RedirectToPage(new { envId, project });
    }
}
