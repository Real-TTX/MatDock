using MatDock.Core.Entities;

namespace MatDock.Core.Git;

/// <summary>Create/update carrier for a Git credential. On edit, a blank <see cref="Token"/> keeps the stored one.</summary>
public sealed class GitCredentialInput
{
    public string Name { get; set; } = string.Empty;
    public GitAuthType AuthType { get; set; } = GitAuthType.HttpsToken;
    public string? Username { get; set; }
    public string? Token { get; set; }
}
