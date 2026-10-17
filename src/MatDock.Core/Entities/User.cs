namespace MatDock.Core.Entities;

/// <summary>A local application user (Entra ID comes later).</summary>
public class User : AuditableEntity
{
    public string Username { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash produced by <c>IPasswordHasher</c> (never the plain password).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    public bool IsActive { get; set; } = true;

    /// <summary>Forces a password change on next login (e.g. for the seeded admin).</summary>
    public bool MustChangePassword { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>The stable id (<c>sub</c>) of the person at MatIdentity this account is linked with, or null (a local-only account).
    /// Set at the first sign-in through MatIdentity, or by linking after the account's own password (MatPlatform pending link).</summary>
    public string? ExternalSubject { get; set; }

    public ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
}
