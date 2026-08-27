namespace MatDock.Core.Entities;

/// <summary>
/// A MatDock-managed compose stack: a name (= compose project), the target environment and the stored
/// compose YAML. Deploying writes the YAML to <c>~/.matdock/stacks/&lt;name&gt;/docker-compose.yml</c> on the
/// host and runs <c>docker compose -p &lt;name&gt; up -d</c>. Mapped to the <c>Stack</c> table.
/// </summary>
public class Stack : AuditableEntity
{
    /// <summary>Compose project name (lowercase, [a-z0-9_-], starts alphanumeric).</summary>
    public string Name { get; set; } = string.Empty;

    public long EnvironmentId { get; set; }

    public string ComposeYaml { get; set; } = string.Empty;

    public DateTime? LastDeployedAt { get; set; }

    public string? LastStatus { get; set; }
}
