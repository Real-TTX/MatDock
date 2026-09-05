using MatDock.Core.Entities;

namespace MatDock.Core.Sync;

/// <summary>Create/update carrier for a <see cref="SyncJob"/> and its selected compose files.</summary>
public sealed class SyncJobInput
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string GitRepoUrl { get; set; } = string.Empty;

    public string? GitReference { get; set; }

    public long? GitCredentialId { get; set; }

    public string? Cron { get; set; }

    public bool Enabled { get; set; } = true;

    public SyncUpdateMode UpdateMode { get; set; } = SyncUpdateMode.OnGitChange;

    public bool PullImages { get; set; }

    public bool PruneRemoved { get; set; }

    public List<SyncItemInput> Items { get; set; } = new();
}

/// <summary>One selected compose file mapped to a target environment.</summary>
public sealed class SyncItemInput
{
    public string ComposePath { get; set; } = string.Empty;

    public long EnvironmentId { get; set; }

    public string StackName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
