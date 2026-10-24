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

    /// <summary>What <c>docker inspect</c> says (environment masked); null when it could not be read.</summary>
    public ContainerInspect? Inspect { get; private set; }

    /// <summary>The processes of a running container.</summary>
    public ContainerProcessTable? Processes { get; private set; }
    public string? ProcessesError { get; private set; }

    public bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));
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
            if (container is not null)
            {
                Inspect = await _containerService.InspectAsync(Environment, container.Id.Length > 0 ? container.Id : Id, HttpContext.RequestAborted);
                if (container.IsRunning)
                {
                    (Processes, ProcessesError) = await _containerService.TopAsync(Environment, container.Id.Length > 0 ? container.Id : Id, HttpContext.RequestAborted);
                }
            }

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

    /// <summary>Removes the container (admins only): a running one is stopped first; its anonymous volumes go too. Back to the list afterwards.</summary>
    public async Task<IActionResult> OnPostRemoveAsync(long envId, string id, string? name = null)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            return NotFound();
        }

        var (ok, message) = await _containerService.RemoveAsync(env, id, force: true, volumes: true, HttpContext.RequestAborted);
        StatusMessage = $"Remove {(string.IsNullOrWhiteSpace(name) ? id : name)}: {message}";
        IsError = !ok;
        return ok ? RedirectToPage("Index") : RedirectToPage(new { envId, id });
    }

    /// <summary>Changes the memory and CPU limits and the restart policy (admins only); an empty field stays as it is.</summary>
    public async Task<IActionResult> OnPostLimitsAsync(long envId, string id, string? memory, string? cpus, string? restart)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            return NotFound();
        }

        static string? Value(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Replace(',', '.');
        var (ok, message) = await _containerService.UpdateAsync(env, id, new ContainerUpdateSpec(Value(memory), Value(cpus), Value(restart)), HttpContext.RequestAborted);
        StatusMessage = ok ? "Limits and restart policy updated." : message;
        IsError = !ok;
        return RedirectToPage(new { envId, id });
    }

    /// <summary>Recreates the container with its settings and the newest image of its tag (admins only, not for a container of a stack).</summary>
    public async Task<IActionResult> OnPostRecreateAsync(long envId, string id, bool pull = true)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            return NotFound();
        }

        var (ok, message) = await _containerService.RecreateAsync(env, id, pull, log: null, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return ok ? RedirectToPage("Index") : RedirectToPage(new { envId, id });
    }

    /// <summary>Gives the container another name (admins only).</summary>
    public async Task<IActionResult> OnPostRenameAsync(long envId, string id, string? newName)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            return NotFound();
        }

        var (ok, message) = await _containerService.RenameAsync(env, id, newName ?? string.Empty, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage(new { envId, id });
    }

    public async Task<IActionResult> OnPostActionAsync(long envId, string id, ContainerAction action, string? name = null)
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
        StatusMessage = $"{action} {(string.IsNullOrWhiteSpace(name) ? id : name)}: {message}";
        IsError = !ok;
        return RedirectToPage(new { envId, id });
    }
}
