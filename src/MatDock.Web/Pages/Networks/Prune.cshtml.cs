using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Networks;

/// <summary>Preview + confirm for <c>docker network prune</c> across the selected environment(s).</summary>
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

    public long EnvId { get; private set; }
    public List<DockerEnvironment> Environments { get; private set; } = new();
    public List<EnvGroup> Groups { get; private set; } = new();
    public List<(string Environment, string Message)> Errors { get; private set; } = new();
    public List<PruneOutcome>? Results { get; private set; }

    public int TotalUnused => Groups.Sum(g => g.Networks.Count);

    public sealed record EnvGroup(DockerEnvironment Environment, List<string> Networks);
    public sealed record PruneOutcome(string Environment, bool Success, int Removed, string Detail);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        var targets = await ResolveTargetsAsync();
        var results = new List<PruneOutcome>();
        foreach (var env in targets)
        {
            var r = await _connectionService.PruneNetworksAsync(_environmentService.BuildSettings(env), HttpContext.RequestAborted);
            results.Add(new PruneOutcome(env.Name, r.Success, r.Removed, r.Detail));
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
                    var (_, unused) = await _connectionService.ListNetworksWithUsageAsync(
                        _environmentService.BuildSettings(env), HttpContext.RequestAborted);
                    var names = unused.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    return (env, names, error: (string?)null);
                }
                catch (Exception ex)
                {
                    return (env, names: new List<string>(), error: ex.Message);
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
        foreach (var (env, names, error) in results)
        {
            if (error is not null)
            {
                Errors.Add((env.Name, error));
            }
            else if (names.Count > 0)
            {
                Groups.Add(new EnvGroup(env, names));
            }
        }
    }
}
