namespace MatDock.Core.Entities;

/// <summary>
/// One compose file of a <see cref="SyncJob"/> mapped to a target environment. Each item becomes a
/// managed <see cref="Stack"/> (project name = <see cref="StackName"/>) on deploy. The same compose
/// path may appear multiple times (once per target environment). Mapped to the <c>SyncJobItem</c> table.
/// </summary>
public class SyncJobItem : AuditableEntity
{
    public long SyncJobId { get; set; }

    /// <summary>Repo-relative path to the compose file (e.g. <c>apps/web/docker-compose.yml</c>).</summary>
    public string ComposePath { get; set; } = string.Empty;

    /// <summary>Target environment for this compose file.</summary>
    public long EnvironmentId { get; set; }

    /// <summary>Compose project name / managed stack name (lowercase, [a-z0-9_-], starts alphanumeric).</summary>
    public string StackName { get; set; } = string.Empty;

    /// <summary>Whether this compose is included in the sync (unchecked = excluded, prune candidate).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The managed <see cref="Stack"/> this item deploys to, once created.</summary>
    public long? StackId { get; set; }

    public DateTime? LastDeployedAt { get; set; }

    public string? LastStatus { get; set; }
}
