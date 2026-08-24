namespace MatDock.Core.Security;

/// <summary>
/// Encrypts and decrypts small secrets (SSH passwords, private keys, passphrases) before they are
/// stored in SQLite. Backed by ASP.NET Core Data Protection whose keys live on the mounted data
/// volume, so encrypted values remain readable across container restarts.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);

    string? ProtectNullable(string? plaintext);

    string? UnprotectNullable(string? protectedValue);
}
