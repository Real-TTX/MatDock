using MatDock.Core.Containers;

namespace MatDock.Core.Updates;

/// <summary>
/// What several looks for updates in a row share ("Check all", a pass of the automation, a page that needs the report and the plan): the containers
/// and the local images of a host are read once and not once per stack, and a registry is asked once per image and once per repository. Without it
/// twenty stacks on one host cost twenty <c>docker ps</c> and twenty <c>docker images</c>. For one caller at a time (not thread-safe), and short-lived:
/// what it read does not change under it, so after something was updated on a host <see cref="Forget"/> it.
/// </summary>
public sealed class UpdateCheckBatch
{
    /// <summary>Read all containers of a host once (many stacks are looked at); false: only the registry answers are shared, the host is asked per stack.</summary>
    internal bool WholeHost { get; init; } = true;

    internal Dictionary<long, IReadOnlyList<DockerContainer>> Containers { get; } = new();

    internal Dictionary<long, Dictionary<string, string?>> LocalDigests { get; } = new();

    internal Dictionary<string, string?> RegistryDigests { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, IReadOnlyList<string>> Tags { get; } = new(StringComparer.Ordinal);

    /// <summary>For the report and the plan of one stack: the registry answers are shared, the host is asked for this stack only.</summary>
    public static UpdateCheckBatch ForOneStack() => new() { WholeHost = false };

    /// <summary>Forgets what was read on a host (its containers and images changed: something was updated there).</summary>
    public void Forget(long environmentId)
    {
        Containers.Remove(environmentId);
        LocalDigests.Remove(environmentId);
    }
}
