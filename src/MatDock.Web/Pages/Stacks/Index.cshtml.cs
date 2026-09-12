using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Stacks;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace MatDock.Web.Pages.Stacks;

public class IndexModel : PageModel
{
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly StackService _stackService;
    private readonly EnvironmentService _environmentService;
    private readonly ContainerService _containerService;
    private readonly BundleBackupService _bundleService;
    private readonly BackupTargetService _targetService;
    private readonly IMemoryCache _cache;

    public IndexModel(StackService stackService, EnvironmentService environmentService, ContainerService containerService,
        BundleBackupService bundleService, BackupTargetService targetService, IMemoryCache cache)
    {
        _stackService = stackService;
        _environmentService = environmentService;
        _containerService = containerService;
        _bundleService = bundleService;
        _targetService = targetService;
        _cache = cache;
    }

    /// <summary>Globally selected environment (0 = all), from the sidebar dropdown / cookie.</summary>
    public long EnvId { get; private set; }
    public bool IsAll => EnvId <= 0;

    [BindProperty(SupportsGet = true)]
    public string View { get; set; } = "list";

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    public List<StackRow> Rows { get; private set; } = new();
    public int TotalCount { get; private set; }
    public List<string> LoadErrors { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }
    [TempData] public string? Output { get; set; }

    public sealed record StackRow(
        long? ManagedId, long EnvId, string EnvName, string Name, bool Managed, bool Discovered,
        bool GitBacked, int Running, int Total, DateTime? LastDeployedAt, string? LastStatus,
        IReadOnlyList<DockerContainer> Containers, string? EnvHost, string? AppMetaJson)
    {
        public bool IsRunning => Total > 0 && Running == Total;
        public bool IsPartial => Total > 0 && Running > 0 && Running < Total;

        /// <summary>Normalized status for filtering/display: running, partial or stopped.</summary>
        public string StatusKind => IsRunning ? "running" : IsPartial ? "partial" : "stopped";
    }

    public async Task OnGetAsync()
    {
        EnvId = EnvSelection.Resolve(HttpContext) ?? 0;

        var allEnvs = await _environmentService.GetAllAsync(HttpContext.RequestAborted);
        var envById = allEnvs.ToDictionary(e => e.Id);

        // Stale selection (env deleted or deactivated): fall back to "all" and clear the cookie.
        if (EnvId > 0 && !allEnvs.Any(e => e.Id == EnvId && e.IsEnabled))
        {
            EnvId = 0;
            EnvSelection.Clear(HttpContext);
        }

        var managed = await _stackService.GetAllAsync(HttpContext.RequestAborted);
        if (!IsAll)
        {
            managed = managed.Where(s => s.EnvironmentId == EnvId).ToList();
        }

        // Discover compose projects (running or stopped) on the target hosts — the selected env, or all
        // enabled envs when "all" is chosen (parallel, best-effort; per-env errors surfaced inline).
        var enabled = allEnvs.Where(e => e.IsEnabled && (IsAll || e.Id == EnvId)).ToList();
        var discovered = new Dictionary<(long, string), (int Running, int Total, List<DockerContainer> Containers)>();

        // Cap concurrent host connections so opening "all" never triggers an SSH auth storm / lockout.
        using var gate = new SemaphoreSlim(4);
        var scans = enabled.Select(async env =>
        {
            // Serve container+stats from the cache shared with the Dashboard so aggregated stack
            // resources are available without re-sampling on every visit.
            if (_cache.TryGetValue($"dash:containers:{env.Id}", out IReadOnlyList<DockerContainer>? cached) && cached is not null)
            {
                return (env, cached, error: (string?)null);
            }

            await gate.WaitAsync(HttpContext.RequestAborted);
            try
            {
                var containers = await _containerService.ListAsync(env, HttpContext.RequestAborted, includeStats: true);
                _cache.Set($"dash:containers:{env.Id}", containers, TimeSpan.FromSeconds(30));
                return (env, containers, error: (string?)null);
            }
            catch (Exception ex)
            {
                return (env, (IReadOnlyList<DockerContainer>)Array.Empty<DockerContainer>(), error: ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });
        var results = await Task.WhenAll(scans);

        foreach (var (env, containers, error) in results)
        {
            if (error is not null)
            {
                LoadErrors.Add($"{env.Name}: {error}");
                continue;
            }

            foreach (var group in containers.Where(c => !string.IsNullOrEmpty(c.Project)).GroupBy(c => c.Project!))
            {
                var running = group.Count(c => c.IsRunning);
                // A cleanly-finished one-shot/init container (Exited 0) shouldn't make an otherwise-up stack
                // look partial; a crashed (non-zero) container still counts as not-running.
                var total = running > 0
                    ? running + group.Count(c => !c.IsRunning && !IsCleanExit(c))
                    : group.Count();
                var ordered = group
                    .OrderByDescending(c => c.IsRunning)
                    .ThenBy(c => c.Service ?? c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                discovered[(env.Id, group.Key)] = (running, total, ordered);
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
                live.Running, live.Total, s.LastDeployedAt, s.LastStatus,
                live.Containers ?? (IReadOnlyList<DockerContainer>)Array.Empty<DockerContainer>(),
                HostFor(envById, s.EnvironmentId), s.AppMetaJson));
        }

        foreach (var ((envId, project), d) in discovered)
        {
            if (managedKeys.Contains((envId, project)))
            {
                continue;
            }

            rows.Add(new StackRow(null, envId, EnvName(envById, envId), project,
                Managed: false, Discovered: true, GitBacked: false, d.Running, d.Total, null, null, d.Containers,
                HostFor(envById, envId), null));
        }

        rows = rows.OrderBy(r => r.EnvName).ThenByDescending(r => r.Managed).ThenBy(r => r.Name).ToList();
        TotalCount = rows.Count;

        // ---- Client-invisible server-side filtering (search + status + type) ----
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            rows = rows.Where(r => r.Name.Contains(q, Ic) || r.EnvName.Contains(q, Ic)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(Status))
        {
            rows = rows.Where(r => string.Equals(r.StatusKind, Status, Ic)).ToList();
        }

        if (string.Equals(Type, "managed", Ic))
        {
            rows = rows.Where(r => r.Managed).ToList();
        }
        else if (string.Equals(Type, "external", Ic))
        {
            rows = rows.Where(r => r.Discovered).ToList();
        }

        Rows = rows;
    }

    private static string EnvName(IReadOnlyDictionary<long, DockerEnvironment> byId, long id)
        => byId.TryGetValue(id, out var e) ? e.Name : $"#{id}";

    private static string? HostFor(IReadOnlyDictionary<long, DockerEnvironment> byId, long id)
        => byId.TryGetValue(id, out var e) ? MatDock.Web.Support.OpenUrls.PortLinkHost(e) : null;

    private static bool IsCleanExit(DockerContainer c)
        => string.Equals(c.State, "exited", StringComparison.OrdinalIgnoreCase) && c.Status.Contains("(0)", StringComparison.Ordinal);

    public async Task<IActionResult> OnPostDeployAsync(long id)
    {
        var (ok, output) = await _stackService.DeployAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? "Stack deployed." : "Deploy failed.";
        IsError = !ok;
        Output = output;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDownAsync(long id)
    {
        var (ok, output) = await _stackService.DownAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? "Stack stopped." : "Stop failed.";
        IsError = !ok;
        Output = output;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStopAsync(long id)
    {
        var (ok, output) = await _stackService.StopAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? "Stack stopped." : "Stop failed.";
        IsError = !ok;
        Output = output;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStartAsync(long id)
    {
        var (ok, output) = await _stackService.StartAsync(id, HttpContext.RequestAborted);
        StatusMessage = ok ? "Stack started." : "Start failed.";
        IsError = !ok;
        Output = output;
        return RedirectToPage();
    }

    public Task<IActionResult> OnPostProjectStartAsync(long envId, string project) => ProjectActionAsync(envId, project, "start");
    public Task<IActionResult> OnPostProjectStopAsync(long envId, string project) => ProjectActionAsync(envId, project, "stop");
    public Task<IActionResult> OnPostProjectRestartAsync(long envId, string project) => ProjectActionAsync(envId, project, "restart");

    /// <summary>Lifecycle for a discovered (external) compose project — by project label, no managed record.</summary>
    private async Task<IActionResult> ProjectActionAsync(long envId, string project, string verb)
    {
        var redirect = new { view = View, Q, Status, Type };
        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(redirect);
        }

        var (ok, output) = verb switch
        {
            "start" => await _stackService.ProjectStartAsync(env, project, HttpContext.RequestAborted),
            "stop" => await _stackService.ProjectStopAsync(env, project, HttpContext.RequestAborted),
            _ => await _stackService.ProjectRestartAsync(env, project, HttpContext.RequestAborted),
        };
        StatusMessage = ok ? $"Stack '{project}' {verb}." : $"{verb} failed.";
        IsError = !ok;
        Output = output;
        return RedirectToPage(redirect);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _stackService.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Stack deleted (containers may still be running – stop them first)." : "Stack not found.";
        IsError = !deleted;
        return RedirectToPage();
    }

    /// <summary>Full backup of a stack (all volumes + compose + image refs) to the default target.</summary>
    public async Task<IActionResult> OnPostBundleBackupAsync(long envId, string project)
    {
        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(new { view = View, Q, Status, Type });
        }

        var target = await _targetService.GetDefaultAsync(HttpContext.RequestAborted);
        var (ok, message) = await _bundleService.BackupStackAsync(env, project, target, stopContainers: false, HttpContext.RequestAborted);
        StatusMessage = $"{message} → {(target?.Name ?? "Local")}";
        IsError = !ok;
        return RedirectToPage(new { view = View, Q, Status, Type });
    }

    /// <summary>Start/stop/restart a single container from an expanded stack, staying on the current view.</summary>
    public async Task<IActionResult> OnPostContainerActionAsync(long envId, string id, ContainerAction action)
    {
        var env = await _environmentService.GetAsync(envId, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            StatusMessage = "Environment not available (disabled or deleted).";
            IsError = true;
            return RedirectToPage(new { view = View, Q, Status, Type });
        }

        var (ok, message) = await _containerService.ActionAsync(env, id, action, HttpContext.RequestAborted);
        StatusMessage = $"{action} {id}: {message}";
        IsError = !ok;
        return RedirectToPage(new { view = View, Q, Status, Type });
    }
}
