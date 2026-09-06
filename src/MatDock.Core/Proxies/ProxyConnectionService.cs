using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Proxies;

/// <summary>CRUD for proxy connections plus route operations dispatched to the matching provider.</summary>
public sealed class ProxyConnectionService
{
    private readonly MatDockDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IEnumerable<IProxyProvider> _providers;

    public ProxyConnectionService(MatDockDbContext db, ISecretProtector secrets, IEnumerable<IProxyProvider> providers)
    {
        _db = db;
        _secrets = secrets;
        _providers = providers;
    }

    public Task<List<ProxyConnection>> GetAllAsync(CancellationToken ct = default)
        => _db.ProxyConnections.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

    public Task<ProxyConnection?> GetAsync(long id, CancellationToken ct = default)
        => _db.ProxyConnections.FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>True when at least one proxy connection exists (drives the conditional "Proxy" menu item).</summary>
    public Task<bool> AnyAsync(CancellationToken ct = default)
        => _db.ProxyConnections.AnyAsync(ct);

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(ProxyInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return (false, error, 0);
        }

        var conn = new ProxyConnection();
        Apply(conn, input);
        _db.ProxyConnections.Add(conn);
        await _db.SaveChangesAsync(ct);
        return (true, "Proxy connection saved.", conn.Id);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(long id, ProxyInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return (false, error);
        }

        var conn = await _db.ProxyConnections.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (conn is null)
        {
            return (false, "Proxy connection not found.");
        }

        Apply(conn, input);
        await _db.SaveChangesAsync(ct);
        return (true, "Proxy connection saved.");
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var conn = await _db.ProxyConnections.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (conn is null)
        {
            return false;
        }

        _db.ProxyConnections.Remove(conn);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ProxyResult> TestAsync(long id, CancellationToken ct = default)
    {
        var (conn, provider, target) = await ResolveAsync(id, ct);
        if (conn is null)
        {
            return ProxyResult.Fail("Proxy connection not found.");
        }
        return provider is null ? ProxyResult.Fail("No provider for this proxy type.") : await provider.TestAsync(target!, ct);
    }

    /// <summary>Tests unsaved form input (blank token on edit falls back to the stored one).</summary>
    public async Task<ProxyResult> TestInputAsync(ProxyInput input, long? existingId, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return ProxyResult.Fail(error);
        }

        var provider = _providers.FirstOrDefault(p => p.Type == input.Provider);
        if (provider is null)
        {
            return ProxyResult.Fail("No provider for this proxy type.");
        }

        var token = input.Token;
        if (string.IsNullOrWhiteSpace(token) && existingId is > 0)
        {
            var conn = await GetAsync(existingId.Value, ct);
            token = _secrets.UnprotectNullable(conn?.EncryptedToken);
        }

        return await provider.TestAsync(
            new ProxyTarget(input.Url.Trim(), string.IsNullOrWhiteSpace(input.ServerName) ? null : input.ServerName.Trim(), token), ct);
    }

    public async Task<IReadOnlyList<ProxyRoute>> ListRoutesAsync(long id, CancellationToken ct = default)
    {
        var (conn, provider, target) = await ResolveAsync(id, ct);
        if (conn is null || provider is null)
        {
            return Array.Empty<ProxyRoute>();
        }
        return await provider.ListRoutesAsync(target!, ct);
    }

    public async Task<ProxyResult> AddRouteAsync(long id, ProxyRouteInput route, CancellationToken ct = default)
    {
        var (conn, provider, target) = await ResolveAsync(id, ct);
        if (conn is null || provider is null)
        {
            return ProxyResult.Fail("Proxy connection not found.");
        }
        return await provider.AddRouteAsync(target!, route, ct);
    }

    public async Task<ProxyResult> RemoveRouteAsync(long id, string routeId, CancellationToken ct = default)
    {
        var (conn, provider, target) = await ResolveAsync(id, ct);
        if (conn is null || provider is null)
        {
            return ProxyResult.Fail("Proxy connection not found.");
        }
        return await provider.RemoveRouteAsync(target!, routeId, ct);
    }

    private async Task<(ProxyConnection? Conn, IProxyProvider? Provider, ProxyTarget? Target)> ResolveAsync(long id, CancellationToken ct)
    {
        var conn = await _db.ProxyConnections.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (conn is null)
        {
            return (null, null, null);
        }

        var provider = _providers.FirstOrDefault(p => p.Type == conn.Provider);
        var token = _secrets.UnprotectNullable(conn.EncryptedToken);
        return (conn, provider, new ProxyTarget(conn.Url, conn.ServerName, token));
    }

    private void Apply(ProxyConnection conn, ProxyInput input)
    {
        conn.Name = input.Name.Trim();
        conn.Provider = input.Provider;
        conn.Url = input.Url.Trim();
        conn.ServerName = string.IsNullOrWhiteSpace(input.ServerName) ? null : input.ServerName.Trim();
        conn.Enabled = input.Enabled;

        // Blank token on edit keeps the stored one.
        if (!string.IsNullOrWhiteSpace(input.Token))
        {
            conn.EncryptedToken = _secrets.Protect(input.Token.Trim());
        }
    }

    private static string? Validate(ProxyInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Please enter a name.";
        }

        var url = (input.Url ?? string.Empty).Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "URL must start with http:// or https://.";
        }

        return null;
    }
}
