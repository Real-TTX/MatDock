using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Containers;

public class LogsModel : PageModel
{
    private readonly EnvironmentService _environmentService;

    public LogsModel(EnvironmentService environmentService)
    {
        _environmentService = environmentService;
    }

    [BindProperty(SupportsGet = true)]
    public long EnvId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    /// <summary>The compose project whose containers are shown together (instead of the one container <see cref="Id"/>).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Stack { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Tail { get; set; } = 200;

    public DockerEnvironment? Environment { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Environment = await _environmentService.GetAsync(EnvId, HttpContext.RequestAborted);
        if (Environment is null)
        {
            return NotFound();
        }

        // The live log stream is delivered over the /logs/ws WebSocket (docker logs -f).
        return Page();
    }
}
