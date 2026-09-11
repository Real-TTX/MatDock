using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Registries;

/// <summary>CRUD for container registries plus test/browse (v2 API) and login resolution for deploys.</summary>
public sealed class RegistryService
{
    private readonly MatDockDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly RegistryApiClient _api;

    public RegistryService(MatDockDbContext db, ISecretProtector secrets, RegistryApiClient api)
    {
        _db = db;
        _secrets = secrets;
        _api = api;
    }

    public Task<List<ContainerRegistry>> GetAllAsync(CancellationToken ct = default)
        => _db.ContainerRegistries.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);

    public Task<ContainerRegistry?> GetAsync(long id, CancellationToken ct = default)
        => _db.ContainerRegistries.FirstOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>True when at least one registry exists (drives the conditional "Registry" menu item).</summary>
    public Task<bool> AnyAsync(CancellationToken ct = default)
        => _db.ContainerRegistries.AnyAsync(ct);

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(RegistryInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return (false, error, 0);
        }

        var reg = new ContainerRegistry();
        Apply(reg, input);
        _db.ContainerRegistries.Add(reg);
        await _db.SaveChangesAsync(ct);
        return (true, "Registry saved.", reg.Id);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(long id, RegistryInput input, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return (false, error);
        }

        var reg = await _db.ContainerRegistries.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reg is null)
        {
            return (false, "Registry not found.");
        }

        Apply(reg, input);
        await _db.SaveChangesAsync(ct);
        return (true, "Registry saved.");
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var reg = await _db.ContainerRegistries.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reg is null)
        {
            return false;
        }

        _db.ContainerRegistries.Remove(reg);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<RegistryResult> TestAsync(long id, CancellationToken ct = default)
    {
        var reg = await GetAsync(id, ct);
        return reg is null ? RegistryResult.Fail("Registry not found.") : await _api.TestAsync(ToLogin(reg), ct);
    }

    /// <summary>Tests unsaved form input (blank token on edit falls back to the stored one).</summary>
    public async Task<RegistryResult> TestInputAsync(RegistryInput input, long? existingId, CancellationToken ct = default)
    {
        var error = Validate(input);
        if (error is not null)
        {
            return RegistryResult.Fail(error);
        }

        var token = input.Token;
        if (string.IsNullOrWhiteSpace(token) && existingId is > 0)
        {
            var reg = await GetAsync(existingId.Value, ct);
            token = _secrets.UnprotectNullable(reg?.EncryptedToken);
        }

        return await _api.TestAsync(new RegistryLogin(input.Host.Trim(), input.Username.Trim(), token, input.Insecure), ct);
    }

    public async Task<RegistryCatalog> ListRepositoriesAsync(long id, CancellationToken ct = default)
    {
        var reg = await GetAsync(id, ct);
        return reg is null
            ? new RegistryCatalog(Array.Empty<string>(), "Registry not found.")
            : await _api.ListRepositoriesAsync(ToLogin(reg), ct: ct);
    }

    public async Task<RegistryTagList> ListTagsAsync(long id, string repo, CancellationToken ct = default)
    {
        var reg = await GetAsync(id, ct);
        return reg is null
            ? new RegistryTagList(repo, Array.Empty<string>(), "Registry not found.")
            : await _api.ListTagsAsync(ToLogin(reg), repo, ct);
    }

    /// <summary>Current manifest digest for an image (host/repo:tag), using configured creds for the host
    /// or anonymous access. Used by update detection. Returns null if unavailable.</summary>
    public async Task<string?> GetDigestForImageAsync(string host, string repo, string tag, CancellationToken ct = default)
    {
        var login = await ResolveLoginForHostAsync(host, ct);
        return await _api.GetDigestAsync(login, repo, tag, ct);
    }

    private async Task<RegistryLogin> ResolveLoginForHostAsync(string host, CancellationToken ct)
    {
        var norm = NormalizeHost(host);
        var regs = await _db.ContainerRegistries.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        var match = regs.FirstOrDefault(r => HostMatches(r.Host, norm));
        return match is not null ? ToLogin(match) : new RegistryLogin(norm, string.Empty, null, Insecure: false);
    }

    private static bool HostMatches(string a, string b)
    {
        static string Canon(string h)
        {
            h = NormalizeHost(h);
            return h is "index.docker.io" ? "docker.io" : h;
        }
        return string.Equals(Canon(a), Canon(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Enabled registries with decrypted credentials, for <c>docker login</c> before a deploy.</summary>
    public async Task<IReadOnlyList<RegistryLogin>> GetEnabledLoginsAsync(CancellationToken ct = default)
    {
        var regs = await _db.ContainerRegistries.AsNoTracking()
            .Where(r => r.Enabled)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
        return regs.Select(ToLogin).ToList();
    }

    private RegistryLogin ToLogin(ContainerRegistry reg)
        => new(reg.Host, reg.Username, _secrets.UnprotectNullable(reg.EncryptedToken), reg.Insecure);

    private void Apply(ContainerRegistry reg, RegistryInput input)
    {
        reg.Name = input.Name.Trim();
        reg.Host = NormalizeHost(input.Host);
        reg.Username = input.Username.Trim();
        reg.Insecure = input.Insecure;
        reg.Enabled = input.Enabled;

        // Blank token on edit keeps the stored one.
        if (!string.IsNullOrWhiteSpace(input.Token))
        {
            reg.EncryptedToken = _secrets.Protect(input.Token.Trim());
        }
    }

    /// <summary>Strips scheme and trailing slash from a host entry (users often paste https://host).</summary>
    private static string NormalizeHost(string host)
    {
        var h = host.Trim();
        if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { h = h["https://".Length..]; }
        else if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { h = h["http://".Length..]; }
        return h.TrimEnd('/');
    }

    private static string? Validate(RegistryInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Please enter a name.";
        }
        if (string.IsNullOrWhiteSpace(input.Host))
        {
            return "Please enter the registry host (e.g. ghcr.io or registry.example.com:5000).";
        }
        var host = NormalizeHost(input.Host);
        if (host.Contains('/') || host.Contains(' '))
        {
            return "Host must be a bare host[:port], without a path.";
        }
        return null;
    }
}
