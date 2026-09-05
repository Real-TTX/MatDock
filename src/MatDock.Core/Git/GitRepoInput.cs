namespace MatDock.Core.Git;

/// <summary>Create/update carrier for a saved <see cref="MatDock.Core.Entities.GitRepo"/>.</summary>
public sealed class GitRepoInput
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string? Reference { get; set; }

    public long? GitCredentialId { get; set; }
}
