using MatDock.Core.Containers;

namespace MatDock.Core.Updates;

/// <summary>How the containers of a stack look after an update.</summary>
public enum StackHealth
{
    /// <summary>All running, no health check failing or still starting.</summary>
    Healthy,

    /// <summary>Not yet decided: a health check is still starting or a container is (re)starting.</summary>
    Starting,

    /// <summary>A container stopped or its health check failed.</summary>
    Failed,
}

/// <summary>The health gate of an update: the stack counts as up when all its containers run and none is unhealthy or starting.</summary>
public static class UpdateHealth
{
    public static (StackHealth Health, string? Reason) Evaluate(IReadOnlyList<DockerContainer> containers)
    {
        if (containers.Count == 0)
        {
            return (StackHealth.Failed, "No container of the stack is there.");
        }

        foreach (var c in containers)
        {
            var status = c.Status ?? string.Empty;
            if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase))
            {
                return (StackHealth.Failed, $"{c.Name} is unhealthy.");
            }

            if (string.Equals(c.State, "exited", StringComparison.OrdinalIgnoreCase) || string.Equals(c.State, "dead", StringComparison.OrdinalIgnoreCase))
            {
                return (StackHealth.Failed, $"{c.Name} stopped ({status}).");
            }
        }

        return containers.All(c => c.IsRunning && !(c.Status ?? string.Empty).Contains("health: starting", StringComparison.OrdinalIgnoreCase))
            ? (StackHealth.Healthy, null)
            : (StackHealth.Starting, null);
    }
}
