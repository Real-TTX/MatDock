using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Volumes;

/// <summary>Preview + confirm for pruning unused volumes, with separate opt-in for local volumes and
/// remote network shares (NFS/CIFS) — network shares are excluded by default.</summary>
public class PruneModel : PageModel
{
    private readonly EnvironmentService _environmentService;
    private readonly IEnvironmentConnectionService _connectionService;

    public PruneModel(EnvironmentService environmentService, IEnvironmentConnectionService connectionService)
    {
        _environmentService = environmentService;
        _connectionService = connectionService;
    }

    /// <summary>Prune scope: null = fall back to the global selection; 0 = all; otherwise a single env.</summary>
    [BindProperty(SupportsGet = true)]
    public long? Env { get; set; }

    /// <summary>Include local volumes (on by default).</summary>
    [BindProperty] public bool IncludeLocal { get; set; } = true;

    /// <summary>Include remote network shares NFS/CIFS (off by default — protects remote mounts).</summary>
    [BindProperty] public bool IncludeShares { get; set; }

    public long EnvId { get; private set; }
    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<EnvGroup> Groups { get; private set; } = new();
    public List<(string Environment, string Message)> Errors { get; private set; } = new();
    public List<PruneOutcome>? Results { get; private set; }

    public int TotalUnused => Groups.Sum(g => g.Volumes.Count);
    public int LocalCount => Groups.Sum(g => g.Volumes.Count(v => !v.IsNetworkShare));
    public int ShareCount => Groups.Sum(g => g.Volumes.Count(v => v.IsNetworkShare));

    public sealed record EnvGroup(DockerEnvironment Environment, List<DockerUnusedVolume> Volumes);
    public sealed record PruneOutcome(string Environment, bool Success, int Removed, string Detail);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();

        if (!IncludeLocal && !IncludeShares)
        {
            Results = new List<PruneOutcome>();
            return Page();
        }

        var results = new List<PruneOutcome>();
        foreach (var group in Groups)
        {
            // Remove exactly the volumes in the selected categories (targeted rm, not `volume prune`,
            // so network shares can be protected — prune cannot filter by driver type).
            var names = group.Volumes
                .Where(v => v.IsNetworkShare ? IncludeShares : IncludeLocal)
                .Select(v => v.Name)
                .ToList();
            if (names.Count == 0)
            {
                continue;
            }

            var r = await _connectionService.RemoveVolumesAsync(
                _environmentService.BuildSettings(group.Environment), names, HttpContext.RequestAborted);
            results.Add(new PruneOutcome(group.Environment.Name, r.Success, r.Removed, r.Detail));
        }

        Results = results;
        await LoadAsync();
        return Page();
    }

    private async Task<List<DockerEnvironment>> ResolveTargetsAsync()
    {
        Environments = await _environmentService.GetEnabledAsync(HttpContext.RequestAborted);

        // Explicit ?env / hidden field wins; otherwise use the global (cookie) selection.
        var scope = Env ?? MatDock.Web.Support.EnvSelection.Resolve(HttpContext) ?? 0;
        if (scope > 0 && Environments.All(e => e.Id != scope))
        {
            scope = 0;
        }

        EnvId = scope;
        return (scope > 0 ? Environments.Where(e => e.Id == scope) : Environments).ToList();
    }

    private async Task LoadAsync()
    {
        var targets = await ResolveTargetsAsync();

        using var gate = new SemaphoreSlim(4);
        var tasks = targets.Select(async env =>
        {
            await gate.WaitAsync(HttpContext.RequestAborted);
            try
            {
                try
                {
                    var unused = await _connectionService.ListUnusedVolumesDetailedAsync(
                        _environmentService.BuildSettings(env), HttpContext.RequestAborted);
                    var vols = unused.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    return (env, vols, error: (string?)null);
                }
                catch (Exception ex)
                {
                    return (env, vols: new List<DockerUnusedVolume>(), error: ex.Message);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        Groups = new List<EnvGroup>();
        Errors = new List<(string, string)>();
        foreach (var (env, vols, error) in results)
        {
            if (error is not null)
            {
                Errors.Add((env.Name, error));
            }
            else if (vols.Count > 0)
            {
                Groups.Add(new EnvGroup(env, vols));
            }
        }
    }
}
