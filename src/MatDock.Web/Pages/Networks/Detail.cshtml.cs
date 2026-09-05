using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Networks;

public class DetailModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;

    public DetailModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
    }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public long EnvId { get; private set; }
    public string NetworkName { get; private set; } = string.Empty;
    public DockerEnvironment? Environment { get; private set; }
    public DockerNetworkDetail? Detail { get; private set; }
    public string? Error { get; private set; }

    public bool IsPredefined => NetworkName is "bridge" or "host" or "none";

    public async Task<IActionResult> OnGetAsync(long envId, string network)
    {
        EnvId = envId;
        NetworkName = network ?? string.Empty;

        Environment = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (Environment is null || !Environment.IsEnabled)
        {
            return NotFound();
        }

        try
        {
            Detail = await _connectionService.InspectNetworkAsync(
                _environmentService.BuildSettings(Environment), NetworkName, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(long envId, string network)
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
            return RedirectToPage("/Networks/Index", new { EnvId = envId });
        }

        var (ok, message) = await _connectionService.RemoveNetworkAsync(
            _environmentService.BuildSettings(env), network ?? string.Empty, HttpContext.RequestAborted);

        if (ok)
        {
            StatusMessage = message;
            IsError = false;
            return RedirectToPage("/Networks/Index", new { EnvId = envId, Refresh = true });
        }

        // Removal failed (e.g. still in use) — stay on the detail page with the error.
        EnvId = envId;
        NetworkName = network ?? string.Empty;
        Environment = env;
        Error = message;
        try
        {
            Detail = await _connectionService.InspectNetworkAsync(
                _environmentService.BuildSettings(env), NetworkName, HttpContext.RequestAborted);
        }
        catch
        {
            // best-effort re-inspect
        }

        return Page();
    }
}
