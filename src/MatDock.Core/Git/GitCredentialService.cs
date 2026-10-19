using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Git;

/// <summary>Decrypted credential values for use during a clone (never persisted or logged).</summary>
public sealed record GitCredentialSecret(string? Username, string? Token)
{
    /// <summary>The credentials these come from (to save the host key the first SSH clone sees).</summary>
    public long? CredentialId { get; init; }

    public string? PrivateKeyPem { get; init; }
    public string? Passphrase { get; init; }
    public string? HostKeyFingerprint { get; init; }

    /// <summary>Something to authenticate with (a token for https, a key for ssh).</summary>
    public bool HasSecret => !string.IsNullOrEmpty(Token) || !string.IsNullOrEmpty(PrivateKeyPem);
}

/// <summary>CRUD for Git credentials. Tokens are encrypted at rest via <see cref="ISecretProtector"/>.</summary>
public sealed class GitCredentialService
{
    private readonly MatDockDbContext _db;
    private readonly ISecretProtector _secrets;

    public GitCredentialService(MatDockDbContext db, ISecretProtector secrets)
    {
        _db = db;
        _secrets = secrets;
    }

    public Task<List<GitCredential>> GetAllAsync(CancellationToken ct = default)
        => _db.GitCredentials.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    public Task<GitCredential?> GetAsync(long id, CancellationToken ct = default)
        => _db.GitCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <summary>Loads a credential and decrypts its token for use by the Git layer. Null if not found.</summary>
    public async Task<GitCredentialSecret?> ResolveAsync(long id, CancellationToken ct = default)
    {
        var cred = await _db.GitCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cred is null)
        {
            return null;
        }

        return new GitCredentialSecret(cred.Username, _secrets.UnprotectNullable(cred.EncryptedToken))
        {
            CredentialId = cred.Id,
            PrivateKeyPem = _secrets.UnprotectNullable(cred.EncryptedPrivateKey),
            Passphrase = _secrets.UnprotectNullable(cred.EncryptedPassphrase),
            HostKeyFingerprint = cred.HostKeyFingerprint,
        };
    }

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(GitCredentialInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return (false, "Name is required.", 0);
        }

        if (input.AuthType == GitAuthType.HttpsToken && string.IsNullOrWhiteSpace(input.Token))
        {
            return (false, "Token is required.", 0);
        }

        if (input.AuthType == GitAuthType.SshKey && string.IsNullOrWhiteSpace(input.PrivateKeyPem))
        {
            return (false, "The SSH key is required.", 0);
        }

        var cred = new GitCredential();
        Apply(cred, input, isCreate: true);
        _db.GitCredentials.Add(cred);
        await _db.SaveChangesAsync(ct);
        return (true, "Git credentials created.", cred.Id);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(long id, GitCredentialInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return (false, "Name is required.");
        }

        var cred = await _db.GitCredentials.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cred is null)
        {
            return (false, "Git credentials not found.");
        }

        Apply(cred, input, isCreate: false);
        await _db.SaveChangesAsync(ct);
        return (true, "Git credentials saved.");
    }

    /// <summary>Saves the host key the first SSH clone with these credentials saw (only when none is saved).</summary>
    public async Task RememberHostKeyAsync(long id, string? fingerprint, CancellationToken ct = default)
    {
        var cred = await _db.GitCredentials.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cred is { HostKeyFingerprint: null } && !string.IsNullOrEmpty(fingerprint))
        {
            cred.HostKeyFingerprint = fingerprint;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var cred = await _db.GitCredentials.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cred is null)
        {
            return false;
        }

        _db.GitCredentials.Remove(cred); // soft delete via SaveChanges override
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private void Apply(GitCredential cred, GitCredentialInput input, bool isCreate)
    {
        cred.Name = input.Name.Trim();
        cred.AuthType = input.AuthType;
        cred.Username = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim();

        // Blank secrets on edit keep the stored ones; the other kind of secret is dropped.
        if (input.AuthType == GitAuthType.HttpsToken)
        {
            if (!string.IsNullOrEmpty(input.Token))
            {
                cred.EncryptedToken = _secrets.Protect(input.Token);
            }
            else if (isCreate)
            {
                cred.EncryptedToken = null;
            }

            cred.EncryptedPrivateKey = null;
            cred.EncryptedPassphrase = null;
            cred.HostKeyFingerprint = null;
            return;
        }

        cred.EncryptedToken = null;
        if (!string.IsNullOrWhiteSpace(input.PrivateKeyPem))
        {
            cred.EncryptedPrivateKey = _secrets.Protect(input.PrivateKeyPem.Trim() + "\n");
            cred.EncryptedPassphrase = string.IsNullOrEmpty(input.Passphrase) ? null : _secrets.Protect(input.Passphrase);
        }
        else if (!string.IsNullOrEmpty(input.Passphrase))
        {
            cred.EncryptedPassphrase = _secrets.Protect(input.Passphrase);
        }

        cred.HostKeyFingerprint = string.IsNullOrWhiteSpace(input.HostKeyFingerprint) ? null : input.HostKeyFingerprint.Trim();
    }
}
