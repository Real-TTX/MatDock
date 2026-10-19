using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Networks;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Networks;

/// <summary>The network map of one environment: which containers share which networks.</summary>
public class MapModel : PageModel
{
    private readonly EnvironmentService _environments;
    private readonly ContainerService _containers;

    public MapModel(EnvironmentService environments, ContainerService containers)
    {
        _environments = environments;
        _containers = containers;
    }

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public DockerEnvironment? Environment { get; private set; }
    public NetworkMap? Map { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(long? envId)
    {
        var ct = HttpContext.RequestAborted;
        Environments = (await _environments.GetEnabledAsync(ct)).ToList();
        // One environment at a time: the one asked for, else the one of the menu, else the first.
        var wanted = envId is > 0 ? envId : MatDock.Web.Support.EnvSelection.Current(HttpContext);
        Environment = Environments.FirstOrDefault(e => e.Id == wanted) ?? Environments.FirstOrDefault();
        if (Environment is null)
        {
            return;
        }

        try
        {
            Map = NetworkMap.Build(await _containers.ListAsync(Environment, ct, includeStats: false));
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
