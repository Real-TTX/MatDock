using System.Security.Cryptography;
using System.Text;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Security;

/// <summary>The named API keys: made (the key is answered once), listed, revoked, and found by the key a caller presents.</summary>
public sealed class ApiKeyService
{
    public const string KeyStart = "mdk_";

    /// <summary>The last use is written at most this often (not on every call).</summary>
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    private readonly MatDockDbContext _db;

    public ApiKeyService(MatDockDbContext db) => _db = db;

    public Task<List<ApiKey>> GetAllAsync(CancellationToken ct = default)
        => _db.ApiKeys.AsNoTracking().OrderBy(k => k.Name).ToListAsync(ct);

    public Task<bool> AnyAsync(CancellationToken ct = default) => _db.ApiKeys.AnyAsync(ct);

    /// <summary>Makes a key; answers it (only now) or why not.</summary>
    public async Task<(bool Ok, string Message, string? Key)> CreateAsync(string? name, ApiKeyScope scope, DateTime? expiresAtUtc, CancellationToken ct = default)
    {
        name = name?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            return (false, "Please enter a name (at most 100 characters).", null);
        }

        if (expiresAtUtc is { } expires && expires <= DateTime.UtcNow)
        {
            return (false, "The expiry lies in the past.", null);
        }

        var key = KeyStart + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _db.ApiKeys.Add(new ApiKey { Name = name, Prefix = key[..12], KeyHash = Hash(key), Scope = scope, ExpiresAt = expiresAtUtc });
        await _db.SaveChangesAsync(ct);
        return (true, "Key made. Copy it now: it is not shown again.", key);
    }

    public async Task<bool> RevokeAsync(long id, CancellationToken ct = default)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return false;
        }

        _db.ApiKeys.Remove(key); // soft delete: the key no longer works, the row stays for the audit
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The key a caller presents, when it is one of ours and not expired; notes its use.</summary>
    public async Task<ApiKey?> FindAsync(string? presented, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presented) || !presented.StartsWith(KeyStart, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(presented.Trim());
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        var now = DateTime.UtcNow;
        if (key is null || key.ExpiresAt <= now)
        {
            return null;
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt > TouchEvery)
        {
            key.LastUsedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        return key;
    }

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
