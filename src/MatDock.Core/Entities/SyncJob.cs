namespace MatDock.Core.Entities;

/// <summary>
/// A GitOps-style sync job: one Git repository whose compose files are discovered and deployed as
/// managed <see cref="Stack"/>s across one or more environments. Runs on a cron schedule, via an
/// incoming webhook, or manually. Each selected compose file is a <see cref="SyncJobItem"/>.
/// Mapped to the <c>SyncJob</c> table.
/// </summary>
public class SyncJob : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public string GitRepoUrl { get; set; } = string.Empty;

    /// <summary>Optional branch/tag; null = the repo default branch.</summary>
    public string? GitReference { get; set; }

    /// <summary>Optional <see cref="GitCredential"/> for private repos.</summary>
    public long? GitCredentialId { get; set; }

    /// <summary>Optional 5-field cron (UTC); null/empty = no schedule (webhook/manual only).</summary>
    public string? Cron { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Whether a scheduled/webhook trigger redeploys always or only when the commit changed.</summary>
    public SyncUpdateMode UpdateMode { get; set; } = SyncUpdateMode.OnGitChange;

    /// <summary>Run <c>docker compose pull</c> before <c>up -d</c> (updates :latest images).</summary>
    public bool PullImages { get; set; }

    /// <summary>Tear down (compose down + remove) stacks whose compose was removed from the repo or deselected.</summary>
    public bool PruneRemoved { get; set; }

    /// <summary>Opaque, unguessable token that authenticates the incoming webhook URL. Stored in clear
    /// (looked up by equality, like a session token) — it only triggers a sync, never returns data.</summary>
    public string WebhookToken { get; set; } = string.Empty;

    /// <summary>The commit deployed by the last successful run (for OnGitChange change detection).</summary>
    public string? LastCommitSha { get; set; }

    public DateTime? NextRunAt { get; set; }

    public DateTime? LastRunAt { get; set; }

    public string? LastStatus { get; set; }
}
