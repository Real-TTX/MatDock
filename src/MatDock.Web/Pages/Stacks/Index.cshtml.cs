using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

public class IndexModel : PageModel
{
    private readonly StackService _stackService;
    private readonly EnvironmentService _environmentService;
    private readonly ContainerService _containerService;

    public IndexModel(StackService stackService, EnvironmentService environmentService, ContainerService containerService)
    {
        _stackService = stackService;
        _environmentService = environmentService;
        _containerService = containerService;
    }

    /// <summary>Environment filter (0 = all). Discovered/running stacks are only scanned for a selected env.</summary>
    [BindProperty(SupportsGet = true)]
    public long EnvId { get; set; }

    public List<DockerEnvironment> Environments { get; private set; } = new();
    public DockerEnvironment? SelectedEnvironment { get; private set; }
    public List<StackRow> Rows { get; private set; } = new();
    public string? Error { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }
    [TempData] public string? Output { get; set; }

    /// <summary>A managed and/or currently-running compose stack on a host.</summary>
    public sealed record StackRow(
        long? ManagedId, long EnvId, string EnvName, string Name, bool Managed, bool Discovered,
        bool GitBacked, int Running, int Total, DateTime? LastDeployedAt, string? LastStatus)
    {
        public bool IsRunning => Total > 0 && Running == Total;
        public bool IsPartial => Total > 0 && Running > 0 && Running < Total;
        public bool IsStopped => Total == 0 || Running == 0;
    }

    public async Task OnGetAsync()
    {
        Environments = await _environmentService.GetAllAsync(HttpContext.RequestAborted);
        var envById = Environments.ToDictionary(e => e.Id);
        if (EnvId > 0)
        {
            SelectedEnvironment = envById.GetValueOrDefault(EnvId);
        }

        var managed = await _stackService.GetAllAsync(HttpContext.RequestAborted);
        if (EnvId > 0)
        {
            managed = managed.Where(s => s.EnvironmentId == EnvId).ToList();
        }

        // Discover running compose projects — only for a selected, enabled env (one host call; avoids
        // fanning out SSH to every environment on each page view).
        var discovered = new Dictionary<(long, string), (int Running, int Total)>();
        if (SelectedEnvironment is { IsEnabled: true })
        {
            try
            {
                var containers = await _containerService.ListAsync(SelectedEnvironment, HttpContext.RequestAborted);
                foreach (var group in containers
                             .Where(c => !string.IsNullOrEmpty(c.Project))
                             .GroupBy(c => c.Project!))
                {
                    discovered[(SelectedEnvironment.Id, group.Key)] = (group.Count(c => c.IsRunning), group.Count());
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
        }

        var rows = new List<StackRow>();
        var managedKeys = new HashSet<(long, string)>();
        foreach (var s in managed)
        {
            var key = (s.EnvironmentId, s.Name);
            managedKeys.Add(key);
            var live = discovered.TryGetValue(key, out var d) ? d : default;
            rows.Add(new StackRow(s.Id, s.EnvironmentId, EnvName(envById, s.EnvironmentId), s.Name,
                Managed: true, Discovered: false, GitBacked: s.IsGitBacked,
                Running: live.Running, Total: live.Total, s.LastDeployedAt, s.LastStatus));
        }

        foreach (var ((envId, project), d) in discovered)
        {
            if (managedKeys.Contains((envId, project)))
            {
                continue; // already shown as a managed stack (with its live status merged in)
            }

            rows.Add(new StackRow(null, envId, EnvName(envById, envId), project,
                Managed: false, Discovered: true, GitBacked: false,
                Running: d.Running, Total: d.Total, null, null));
        }

        Rows = rows.OrderBy(r => r.EnvName).ThenByDescending(r => r.Managed).ThenBy(r => r.Name).ToList();
    }

    private static string EnvName(IReadOnlyDictionary<long, DockerEnvironment> byId, long id)
        => byId.TryGetValue(id, out var e) ? e.Name : $"#{id}";

    public async Task<IActionResult> OnPostDeployAsync(long id)
    {
        var (ok, output) = await _stackService.DeployAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? "Stack deployt." : "Deploy fehlgeschlagen.";
        IsError = !ok;
        Output = output;
        return RedirectToPage(new { EnvId });
    }

    public async Task<IActionResult> OnPostDownAsync(long id)
    {
        var (ok, output) = await _stackService.DownAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? "Stack gestoppt." : "Stoppen fehlgeschlagen.";
        IsError = !ok;
        Output = output;
        return RedirectToPage(new { EnvId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _stackService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Stack gelöscht (Container bleiben ggf. laufend – vorher stoppen)." : "Stack nicht gefunden.";
        IsError = !deleted;
        return RedirectToPage(new { EnvId });
    }
}
