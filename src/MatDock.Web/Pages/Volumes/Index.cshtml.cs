using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Volumes;

public class IndexModel : PageModel
{
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;

    public IndexModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
    }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public long EnvId { get; set; }

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<VolumeRow> Rows { get; private set; } = new();
    public List<(string Environment, string Message)> Errors { get; private set; } = new();

    public sealed record VolumeRow(DockerEnvironment Environment, DockerVolume Volume);

    public async Task OnGetAsync()
    {
        Environments = await _environmentService.GetAllAsync(HttpContext.RequestAborted);

        var targets = (EnvId > 0 ? Environments.Where(e => e.Id == EnvId) : Environments).ToList();

        // Build settings up front (uses the scoped DbContext), then fan out the SSH calls in parallel.
        var prepared = targets.Select(e => (Env: e, Settings: _environmentService.BuildSettings(e))).ToList();
        var tasks = prepared.Select(async p =>
        {
            try
            {
                var volumes = await _connectionService.ListVolumesAsync(p.Settings, HttpContext.RequestAborted);
                return (p.Env, Volumes: volumes, Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (p.Env, Volumes: (IReadOnlyList<DockerVolume>)Array.Empty<DockerVolume>(), Error: (string?)ex.Message);
            }
        });

        var results = await Task.WhenAll(tasks);

        var rows = new List<VolumeRow>();
        foreach (var (env, volumes, error) in results)
        {
            if (error is not null)
            {
                Errors.Add((env.Name, error));
                continue;
            }

            rows.AddRange(volumes.Select(v => new VolumeRow(env, v)));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            rows = rows.Where(r =>
                r.Volume.Name.Contains(s, Ic) ||
                r.Volume.Driver.Contains(s, Ic) ||
                (r.Volume.Mountpoint ?? string.Empty).Contains(s, Ic) ||
                r.Environment.Name.Contains(s, Ic)).ToList();
        }

        Rows = rows
            .OrderBy(r => r.Environment.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Volume.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
