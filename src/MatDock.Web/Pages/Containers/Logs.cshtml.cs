using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Containers;

public class LogsModel : PageModel
{
    private readonly ContainerService _containerService;
    private readonly EnvironmentService _environmentService;

    public LogsModel(ContainerService containerService, EnvironmentService environmentService)
    {
        _containerService = containerService;
        _environmentService = environmentService;
    }

    [BindProperty(SupportsGet = true)]
    public long EnvId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public int Tail { get; set; } = 200;

    public DockerEnvironment? Environment { get; private set; }
    public string? Log { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Environment = await _environmentService.GetAsync(EnvId, HttpContext.RequestAborted);
        if (Environment is null)
        {
            return NotFound();
        }

        try
        {
            Log = await _containerService.LogsAsync(Environment, Id, Tail, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }

        return Page();
    }
}
