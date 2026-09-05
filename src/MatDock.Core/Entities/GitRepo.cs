namespace MatDock.Core.Entities;

/// <summary>
/// A saved Git repository definition (URL + branch/tag + optional credentials) that can be reused when
/// creating sync jobs or Git-backed stacks — so the URL/credential pairing is entered once. Mapped to
/// the <c>GitRepo</c> table.
/// </summary>
public class GitRepo : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Optional default branch/tag; null = the repo default branch.</summary>
    public string? Reference { get; set; }

    /// <summary>Optional <see cref="GitCredential"/> for private repos.</summary>
    public long? GitCredentialId { get; set; }
}
