namespace MatDock.Core.Entities;

/// <summary>
/// A stored Git credential used to clone private stack repositories. The token is encrypted at rest via
/// <c>ISecretProtector</c> and never persisted in clear text. Mapped to the <c>GitCredential</c> table.
/// </summary>
public class GitCredential : AuditableEntity
{
    /// <summary>Display name (e.g. "GitHub – matdock deploy").</summary>
    public string Name { get; set; } = string.Empty;

    public GitAuthType AuthType { get; set; } = GitAuthType.HttpsToken;

    /// <summary>HTTPS username (for GitHub a PAT usually works with any non-empty username, e.g. the account name).</summary>
    public string? Username { get; set; }

    /// <summary>Data-Protection-encrypted personal access token / password.</summary>
    public string? EncryptedToken { get; set; }
}
