namespace MatDock.Core.Security;

/// <summary>Hashes and verifies local-account passwords.</summary>
public interface IPasswordHasher
{
    /// <summary>Produces a self-describing hash string that can be stored directly.</summary>
    string Hash(string password);

    /// <summary>Verifies a plaintext password against a stored hash in constant time.</summary>
    bool Verify(string password, string storedHash);
}
