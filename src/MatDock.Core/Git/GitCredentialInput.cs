using MatDock.Core.Entities;

namespace MatDock.Core.Git;

/// <summary>Create/update carrier for a Git credential. On edit, a blank <see cref="Token"/> keeps the stored one.</summary>
public sealed class GitCredentialInput
{
    public string Name { get; set; } = string.Empty;
    public GitAuthType AuthType { get; set; } = GitAuthType.HttpsToken;
    public string? Username { get; set; }
    public string? Token { get; set; }

    /// <summary>SSH key (blank on edit = keep the stored one).</summary>
    public string? PrivateKeyPem { get; set; }

    public string? Passphrase { get; set; }

    /// <summary>The expected host key of the Git server (SHA256:…); blank = learn it at the first clone.</summary>
    public string? HostKeyFingerprint { get; set; }
}
