using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Git;

/// <summary>Decrypted credential values for use during a clone (never persisted or logged).</summary>
public sealed record GitCredentialSecret(string? Username, string? Token);

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

        return new GitCredentialSecret(cred.Username, _secrets.UnprotectNullable(cred.EncryptedToken));
    }

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(GitCredentialInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return (false, "Name is required.", 0);
        }

        if (string.IsNullOrWhiteSpace(input.Token))
        {
            return (false, "Token is required.", 0);
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

        // Blank token on edit keeps the stored one; on create it stays null (create requires a token above).
        if (!string.IsNullOrEmpty(input.Token))
        {
            cred.EncryptedToken = _secrets.Protect(input.Token);
        }
        else if (isCreate)
        {
            cred.EncryptedToken = null;
        }
    }
}
