using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Web.Support;
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

    /// <summary>Globally selected environment (null = all), from the sidebar dropdown / cookie.</summary>
    public long? SelectedEnvId { get; private set; }
    public bool IsAll => SelectedEnvId is null;

    [BindProperty(SupportsGet = true)]
    public string View { get; set; } = "list";

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<ContainerRow> Rows { get; private set; } = new();
    public List<string> LoadErrors { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public sealed record ContainerRow(DockerEnvironment Env, DockerContainer Container);

    public async Task OnGetAsync()
    {
        SelectedEnvId = EnvSelection.Resolve(HttpContext);
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);

        // Stale selection (env deleted or deactivated): fall back to "all" and clear the cookie.
        if (SelectedEnvId is { } sel && Environments.All(e => e.Id != sel))
        {
            SelectedEnvId = null;
            EnvSelection.Clear(HttpContext);
        }

        var targets = IsAll
            ? Environments
            : Environments.Where(e => e.Id == SelectedEnvId).ToList();

        // Cap concurrent host connections so opening "all" never triggers an SSH auth storm / lockout.
        using var gate = new SemaphoreSlim(4);
        var tasks = targets.Select(async env =>
        {
            await gate.WaitAsync(HttpContext.RequestAborted);
            try
            {
                var containers = await _containerService.ListAsync(env, HttpContext.RequestAborted);
                return (env, containers, error: (string?)null);
            }
            catch (Exception ex)
            {
                return (env, containers: (IReadOnlyList<DockerContainer>)Array.Empty<DockerContainer>(), error: (string?)ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        Rows = results
            .SelectMany(r => r.containers.Select(c => new ContainerRow(r.env, c)))
            .OrderBy(r => r.Env.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Container.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        LoadErrors = results.Where(r => r.error is not null).Select(r => $"{r.env.Name}: {r.error}").ToList();
    }

    public async Task<IActionResult> OnPostActionAsync(long envId, string id, ContainerAction action)
    {
        if (!ModelState.IsValid)
        {
            StatusMessage = "Invalid action.";
            IsError = true;
            return RedirectToPage();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage();
        }

        var (ok, message) = await _containerService.ActionAsync(env, id, action, HttpContext.RequestAborted);
        StatusMessage = $"{action} {id}: {message}";
        IsError = !ok;
        return RedirectToPage();
    }
}
