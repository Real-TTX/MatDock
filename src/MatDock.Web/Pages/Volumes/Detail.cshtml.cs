using MatDock.Core.Containers;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Volumes;

public class DetailModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;
    private readonly ContainerService _containerService;

    public DetailModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService, ContainerService containerService)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
        _containerService = containerService;
    }

    public long EnvId { get; private set; }
    public string VolumeName { get; private set; } = string.Empty;
    public DockerEnvironment? Environment { get; private set; }
    public DockerVolumeDetail? Detail { get; private set; }
    public IReadOnlyList<DockerContainer> UsedBy { get; private set; } = new List<DockerContainer>();
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(long envId, string volume)
    {
        EnvId = envId;
        VolumeName = volume ?? string.Empty;

        Environment = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (Environment is null || !Environment.IsEnabled)
        {
            return NotFound();
        }

        try
        {
            Detail = await _connectionService.InspectVolumeAsync(
                _environmentService.BuildSettings(Environment), VolumeName, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }

        // Which containers mount this volume (best-effort; empty on failure).
        UsedBy = await _containerService.ListByVolumeAsync(Environment, VolumeName, HttpContext.RequestAborted);
        return Page();
    }
}
