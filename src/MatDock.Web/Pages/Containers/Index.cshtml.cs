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

    /// <summary>"list" (also when absent) or "gallery". Nullable on purpose: the action forms post no view, and a non-nullable bound
    /// property would make the model state invalid, so every Start/Stop/Restart was answered with "Invalid action".</summary>
    [BindProperty(SupportsGet = true)]
    public string? View { get; set; }

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
        StoppedCount = Rows.Count(r => IsStopped(r.Container));
    }

    /// <summary>How many containers of the shown environments are stopped (exited, created or dead; a paused one is not).</summary>
    public int StoppedCount { get; private set; }

    private static bool IsStopped(DockerContainer c) => !c.IsRunning && !string.Equals(c.State, "paused", StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes one container (admins only); its anonymous volumes go too.</summary>
    public async Task<IActionResult> OnPostRemoveAsync(long envId, string id, string? name = null)
    {
        if (!User.IsInRole(nameof(UserRole.Admin)))
        {
            return Forbid();
        }

        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage();
        }

        var (ok, message) = await _containerService.RemoveAsync(env, id, force: false, volumes: true, HttpContext.RequestAborted);
        StatusMessage = $"Remove {(string.IsNullOrWhiteSpace(name) ? id : name)}: {message}";
        IsError = !ok;
        return RedirectToPage();
    }

    /// <summary>Removes the stopped containers of the chosen environment, or of every environment shown when "All" is chosen (admins only).</summary>
    public async Task<IActionResult> OnPostPruneAsync()
    {
        if (!User.IsInRole(nameof(UserRole.Admin)))
        {
            return Forbid();
        }

        var selected = EnvSelection.Current(HttpContext);
        var targets = (await _environmentService.GetEnabledAsync(HttpContext.RequestAborted))
            .Where(e => selected is null || e.Id == selected)
            .ToList();
        var lines = new List<string>();
        var failed = false;
        foreach (var env in targets)
        {
            var (ok, message) = await _containerService.PruneStoppedAsync(env, HttpContext.RequestAborted);
            failed |= !ok;
            lines.Add(targets.Count > 1 ? $"{env.Name}: {message}" : message);
        }

        StatusMessage = lines.Count == 0 ? "No environment." : string.Join(" ", lines);
        IsError = failed;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostActionAsync(long envId, string id, ContainerAction action, string? name = null)
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
        StatusMessage = $"{action} {(string.IsNullOrWhiteSpace(name) ? id : name)}: {message}";
        IsError = !ok;
        return RedirectToPage();
    }
}
