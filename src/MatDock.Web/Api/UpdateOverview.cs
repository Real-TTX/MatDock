using MatDock.Core.Entities;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using MatDock.Web.Support;

namespace MatDock.Web.Api;

/// <summary>
/// One stack of the update overview: a managed stack (<paramref name="Stack"/>), or a compose project MatDock did not deploy (discovered; the rule of
/// its environment applies and only a moved tag is updated). <paramref name="Plan"/> is what the rule would update, from the last look (null: never
/// looked at). <paramref name="Hints"/>: newer versions the rule does not take. <paramref name="Blocked"/>: why MatDock could not update a discovered
/// project the last time.
/// </summary>
public sealed record UpdateOverviewRow(Stack? Stack, DockerEnvironment Environment, string Name, EffectiveUpdatePolicy Policy, StackUpdatePlan? Plan, DateTime? CheckedUtc,
    IReadOnlyList<(string Service, string Tag)> Hints, string? Blocked = null)
{
    public int Pending => Plan?.Items.Count ?? 0;

    /// <summary>Whether "update all" takes it: something to update, and nothing known that keeps MatDock from doing it.</summary>
    public bool Applicable => Pending > 0 && Blocked is null;
}

/// <summary>
/// The update overview, one place for the page (<c>/Updates</c>), the REST API (<c>/api/v1/updates</c>) and the MCP tools: every stack of the given
/// environments, managed or discovered, with its rule and what the last look found. It asks no registry (a look does: <see cref="UpdateOperations.Check"/>).
/// </summary>
public sealed class UpdateOverview(StackService stacks, UpdatePolicyService policies, UpdateCheckCache cache, HostReadings readings)
{
    /// <summary>The managed stacks of <paramref name="environments"/>, by environment and name.</summary>
    public async Task<List<(Stack Stack, DockerEnvironment Env)>> StacksAsync(IReadOnlyCollection<DockerEnvironment> environments, CancellationToken ct)
    {
        var envs = environments.ToDictionary(e => e.Id);
        return (await stacks.GetAllAsync(ct))
            .Where(s => envs.ContainsKey(s.EnvironmentId))
            .OrderBy(s => envs[s.EnvironmentId].Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => (s, envs[s.EnvironmentId]))
            .ToList();
    }

    /// <summary>The environments among <paramref name="environments"/> whose compose projects MatDock can look at itself (a bridge looks at its own).</summary>
    public static List<DockerEnvironment> ProjectEnvironments(IEnumerable<DockerEnvironment> environments)
        => environments.Where(e => e.ConnectionType != ConnectionType.Bridge).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The compose projects on the hosts of <paramref name="environments"/> that MatDock did not deploy. From the container list the dashboard reads
    /// too (the last one while a new one is taken), so nobody waits for every host; a host that does not answer has none here.
    /// </summary>
    public async Task<List<(DockerEnvironment Env, string Project)>> ProjectsAsync(IReadOnlyCollection<DockerEnvironment> environments, CancellationToken ct)
    {
        var hosts = ProjectEnvironments(environments);
        if (hosts.Count == 0)
        {
            return [];
        }

        var managed = (await stacks.GetAllAsync(ct)).ToLookup(s => s.EnvironmentId, s => s.Name);
        var lists = await Task.WhenAll(hosts.Select(async env => (env, containers: await readings.ContainersAsync(env, ct))));
        return lists
            .SelectMany(l => UpdateService.DiscoveredProjects(l.containers, managed[l.env.Id].ToHashSet(StringComparer.Ordinal)).Select(project => (l.env, project)))
            .ToList();
    }

    /// <summary>Every stack of <paramref name="environments"/>, managed and discovered, by environment and name.</summary>
    public async Task<List<UpdateOverviewRow>> RowsAsync(IReadOnlyCollection<DockerEnvironment> environments, CancellationToken ct)
    {
        var rules = await policies.LoadAsync(ct);
        var managed = (await StacksAsync(environments, ct)).Select(x =>
        {
            var look = cache.Plan(x.Env.Id, x.Stack.Name);
            return new UpdateOverviewRow(x.Stack, x.Env, x.Stack.Name, rules.For(x.Stack), look?.Plan, look?.CheckedUtc, Hints(x.Env.Id, x.Stack.Name, look?.Plan));
        });
        var discovered = (await ProjectsAsync(environments, ct)).Select(p =>
        {
            var look = cache.Plan(p.Env.Id, p.Project);
            return new UpdateOverviewRow(null, p.Env, p.Project, rules.Inherited(UpdatePolicyScope.Stack, p.Env.Id), look?.Plan, look?.CheckedUtc,
                Hints(p.Env.Id, p.Project, look?.Plan), cache.Blocked(p.Env.Id, p.Project));
        });
        return managed.Concat(discovered)
            .OrderBy(r => r.Environment.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The newer versions the last look found that the plan does not take: said beside the stack, nothing happens by itself.</summary>
    private IReadOnlyList<(string Service, string Tag)> Hints(long environmentId, string project, StackUpdatePlan? plan)
        => plan is null || cache.Report(environmentId, project) is not { } look
            ? []
            : look.Report.Images
                .Where(i => i.NewerTag is not null && plan.Items.All(p => p.Service != i.Service))
                .Select(i => (i.Service, i.NewerTag!))
                .ToList();

    /// <summary>An enum value as both answers write it (the MCP tools write their words by hand).</summary>
    private static string Word<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>A row as the REST API and the MCP tools answer it.</summary>
    public static object View(UpdateOverviewRow row) => new
    {
        environment = row.Environment.Name,
        environmentId = row.Environment.Id,
        stack = row.Name,
        managed = row.Stack is not null,
        mode = Word(row.Policy.Mode),
        strategy = Word(row.Policy.Strategy),
        checkedAt = ApiResults.Utc(row.CheckedUtc),
        error = row.Plan?.Error,
        updates = row.Plan?.Items.Select(i => new { service = i.Service, current = i.Current, target = i.Target, kind = Word(i.Kind) }),
        newerVersions = row.Hints.Select(h => new { service = h.Service, tag = h.Tag }),
        blocked = row.Blocked,
    };
}
