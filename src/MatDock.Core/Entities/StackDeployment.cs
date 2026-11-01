namespace MatDock.Core.Entities;

/// <summary>
/// One deploy of a managed stack (<c>docker compose up</c>, also as part of an update, a rollback, a restore or a sync): who or what started it,
/// with which options, how long it took, how it ended and what compose said. A deploy that failed before compose ran (the clone of the repository,
/// a lost environment) is kept too. The newest <see cref="Stacks.DeploymentHistory.Keep"/> per stack stay.
/// </summary>
public class StackDeployment : AuditableEntity
{
    public long StackId { get; set; }

    /// <summary>The stack's name at the time (it stays readable after the stack is deleted).</summary>
    public string StackName { get; set; } = string.Empty;

    public long EnvironmentId { get; set; }

    /// <summary>What started it (<see cref="Stacks.DeployTrigger"/>): manual, webhook, schedule, sync, update, rollback, restore, copy, api, mcp.</summary>
    public string Trigger { get; set; } = Stacks.DeployTrigger.Manual;

    /// <summary>Who started it: a user name, an API key, a schedule; null when nobody is known.</summary>
    public string? Actor { get; set; }

    /// <summary>The options in words (<see cref="Stacks.DeployOptions.Describe"/>), empty for a plain <c>up -d</c>.</summary>
    public string? Options { get; set; }

    /// <summary>The commit of the repository that was deployed (Git stacks).</summary>
    public string? CommitSha { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>Null while it runs (or when MatDock stopped during it), then whether compose succeeded.</summary>
    public bool? Ok { get; set; }

    /// <summary>One line: what compose did (containers created, started …) or why it failed.</summary>
    public string? Summary { get; set; }

    /// <summary>What compose said, the end of it when it was long (<see cref="Stacks.DeploymentHistory.MaxLog"/>).</summary>
    public string? Log { get; set; }
}
