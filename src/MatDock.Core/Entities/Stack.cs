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

    /// <summary>When set, the stack is git-backed: the compose (and any bind-mounted files) come from this repo.</summary>
    public string? GitRepoUrl { get; set; }

    /// <summary>Optional branch/tag to check out; null = the repo default branch.</summary>
    public string? GitReference { get; set; }

    /// <summary>Repo-relative path to the compose file; null/empty = <c>docker-compose.yml</c>.</summary>
    public string? GitComposePath { get; set; }

    /// <summary>Optional <see cref="GitCredential"/> for private repos; null = anonymous/public.</summary>
    public long? GitCredentialId { get; set; }

    public DateTime? LastDeployedAt { get; set; }

    public string? LastStatus { get; set; }

    /// <summary>When set, this stack is managed by a <see cref="SyncJob"/> (created/updated from a repo scan).</summary>
    public long? SyncJobId { get; set; }

    /// <summary>True when the stack is deployed from a Git repository rather than the inline editor.</summary>
    public bool IsGitBacked => !string.IsNullOrWhiteSpace(GitRepoUrl);
}
