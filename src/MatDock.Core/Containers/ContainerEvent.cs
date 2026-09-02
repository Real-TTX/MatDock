namespace MatDock.Core.Containers;

/// <summary>
/// A single container lifecycle event (created/started/stopped/…) as reported by <c>docker events</c>.
/// Times are UTC; <see cref="Kind"/> normalizes the raw docker action into a small display category.
/// </summary>
public sealed record ContainerEvent(DateTime TimeUtc, string Action, string ContainerName, string? Image, string Id)
{
    /// <summary>Normalized lifecycle category for display. "other" = an action we don't surface.</summary>
    public string Kind => Action switch
    {
        "create" => "created",
        "start" => "started",
        "restart" => "restarted",
        "stop" or "die" or "kill" => "stopped",
        "destroy" or "remove" => "removed",
        _ => "other"
    };

    /// <summary>True for the lifecycle actions the dashboard shows (excludes health/exec/pause noise).</summary>
    public bool IsLifecycle => Kind != "other";
}
