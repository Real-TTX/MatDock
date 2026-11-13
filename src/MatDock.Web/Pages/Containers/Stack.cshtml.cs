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
    private readonly MatDock.Core.Stacks.StackService _stacks;

    public StackModel(ContainerService containerService, EnvironmentService environmentService, MatDock.Core.Stacks.StackService stacks)
    {
        _containerService = containerService;
        _environmentService = environmentService;
        _stacks = stacks;
    }

    /// <summary>The stack when MatDock manages it (its services can be restarted, recreated and scaled), else null.</summary>
    public Stack? Managed { get; private set; }

    /// <summary>The services of the stack with how many of their containers run.</summary>
    public IReadOnlyList<(string Service, int Running, int Total)> Services =>
        Containers.Where(c => !string.IsNullOrEmpty(c.Service)).GroupBy(c => c.Service!, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count(c => c.IsRunning), g.Count())).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();

    public bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

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
            Containers = await _containerService.ListByProjectAsync(Environment, Project, HttpContext.RequestAborted, includeStats: true);
            Managed = await _stacks.GetByNameAsync(Environment.Id, Project, HttpContext.RequestAborted);
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

    /// <summary>Restarts, stops, starts, recreates or scales one service of a managed stack (admins only).</summary>
    public async Task<IActionResult> OnPostServiceAsync(long envId, string project, string service, MatDock.Core.Stacks.StackCommands.ServiceAction serviceAction, int replicas = 1)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        var stack = env is { IsEnabled: true } ? await _stacks.GetByNameAsync(env.Id, project ?? string.Empty, HttpContext.RequestAborted) : null;
        if (stack is null || !ModelState.IsValid)
        {
            StatusMessage = stack is null ? "This stack is not managed by MatDock." : "Invalid action.";
            IsError = true;
            return RedirectToPage(new { envId, project });
        }

        var (ok, message) = await _stacks.ServiceActionAsync(stack.Id, service, serviceAction, replicas, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage(new { envId, project });
    }

    public async Task<IActionResult> OnPostActionAsync(long envId, string project, string id, ContainerAction action, string? name = null)
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
        StatusMessage = $"{action} {(string.IsNullOrWhiteSpace(name) ? id : name)}: {message}";
        IsError = !ok;
        return RedirectToPage(new { envId, project });
    }
}
