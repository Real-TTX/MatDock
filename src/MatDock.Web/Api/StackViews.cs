using MatDock.Core.Containers;
using MatDock.Core.Entities;

namespace MatDock.Web.Api;

/// <summary>A container of a stack as the API returns it.</summary>
public sealed record ApiContainer(string Id, string Name, string? Service, string Image, string State, string Status, string? Ports);

/// <summary>A stack in the list: managed (stored in MatDock) or discovered (only running on the host).</summary>
public sealed record ApiStack(
    string Name, long EnvironmentId, bool Managed, string? ManagedBy, string State,
    IReadOnlyList<ApiContainer> Containers, DateTime? UpdatedAt, DateTime? LastDeployedAt, string? LastStatus);

/// <summary>A single stack: the list fields plus its stored compose file and <c>.env</c> (null when not managed).</summary>
public sealed record ApiStackDetail(
    string Name, long EnvironmentId, bool Managed, string? ManagedBy, string State,
    IReadOnlyList<ApiContainer> Containers, DateTime? UpdatedAt, DateTime? LastDeployedAt, string? LastStatus,
    string? ComposeYaml, string? EnvContent);

/// <summary>Builds the API view of stacks from the stored definitions and the live containers of one environment —
/// the same merge of managed and discovered compose projects as the Stacks page.</summary>
public static class StackViews
{
    /// <summary>State used when the containers could not be listed (Docker unreachable).</summary>
    public const string UnknownState = "unknown";

    /// <summary>All stacks of an environment: every managed one plus each compose project that is not managed.</summary>
    public static List<ApiStack> List(long environmentId, IEnumerable<Stack> managed, IReadOnlyList<DockerContainer> containers)
    {
        var byProject = containers
            .Where(c => !string.IsNullOrEmpty(c.Project))
            .GroupBy(c => c.Project!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DockerContainer>)g.ToList(), StringComparer.Ordinal);

        var rows = new List<ApiStack>();
        var managedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stack in managed.Where(s => s.EnvironmentId == environmentId))
        {
            managedNames.Add(stack.Name);
            var live = byProject.TryGetValue(stack.Name, out var c) ? c : Array.Empty<DockerContainer>();
            rows.Add(ToStack(stack, live));
        }

        foreach (var (project, live) in byProject)
        {
            if (!managedNames.Contains(project))
            {
                rows.Add(new ApiStack(project, environmentId, false, null, StateOf(live), Containers(live), null, null, null));
            }
        }

        return rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    public static ApiStack ToStack(Stack stack, IReadOnlyList<DockerContainer> live, string? state = null)
        => new(stack.Name, stack.EnvironmentId, true, stack.ManagedBy, state ?? StateOf(live), Containers(live),
            ApiResults.Utc(stack.UpdateDate), ApiResults.Utc(stack.LastDeployedAt), stack.LastStatus);

    public static ApiStackDetail ToDetail(Stack stack, IReadOnlyList<DockerContainer> live, string? state = null)
        => new(stack.Name, stack.EnvironmentId, true, stack.ManagedBy, state ?? StateOf(live), Containers(live),
            ApiResults.Utc(stack.UpdateDate), ApiResults.Utc(stack.LastDeployedAt), stack.LastStatus,
            stack.ComposeYaml, stack.EnvContent);

    /// <summary>A compose project that runs on the host but is not managed by MatDock.</summary>
    public static ApiStackDetail ToDiscoveredDetail(string project, long environmentId, IReadOnlyList<DockerContainer> live)
        => new(project, environmentId, false, null, StateOf(live), Containers(live), null, null, null, null, null);

    /// <summary>
    /// <c>missing</c> (no containers), <c>stopped</c> (none running), <c>unhealthy</c> (a running container reports
    /// "(unhealthy)"), <c>running</c> (all up; a cleanly finished one-shot container does not count) or <c>partial</c>.
    /// </summary>
    public static string StateOf(IReadOnlyList<DockerContainer> containers)
    {
        if (containers.Count == 0)
        {
            return "missing";
        }

        var running = containers.Count(c => c.IsRunning);
        if (running == 0)
        {
            return "stopped";
        }

        if (containers.Any(c => c.IsRunning && c.Status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase)))
        {
            return "unhealthy";
        }

        var notRunning = containers.Count(c => !c.IsRunning && !IsCleanExit(c));
        return notRunning == 0 ? "running" : "partial";
    }

    public static IReadOnlyList<ApiContainer> Containers(IEnumerable<DockerContainer> containers)
        => containers
            .OrderBy(c => c.Service ?? c.Name, StringComparer.Ordinal)
            .Select(c => new ApiContainer(c.Id, c.Name, c.Service, c.Image, c.State, c.Status, c.Ports))
            .ToList();

    private static bool IsCleanExit(DockerContainer c)
        => string.Equals(c.State, "exited", StringComparison.OrdinalIgnoreCase) && c.Status.Contains("(0)", StringComparison.Ordinal);
}
