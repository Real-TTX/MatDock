using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using MatDock.Core.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Environments;

/// <summary>
/// CRUD and orchestration for <see cref="DockerEnvironment"/>. Owns secret encryption/decryption and
/// translates a stored environment into <see cref="SshConnectionSettings"/> for the connection layer.
/// </summary>
public sealed class EnvironmentService
{
    private readonly MatDockDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IEnvironmentConnectionService _connection;
    private readonly MatDockOptions _options;
    private readonly ICurrentUserAccessor _currentUser;
    private bool _accessLoaded;
    private HashSet<long>? _allowedIds;

    public EnvironmentService(
        MatDockDbContext db,
        ISecretProtector secrets,
        IEnvironmentConnectionService connection,
        IOptions<MatDockOptions> options,
        ICurrentUserAccessor? currentUser = null)
    {
        _db = db;
        _secrets = secrets;
        _connection = connection;
        _options = options.Value;
        _currentUser = currentUser ?? new SystemCurrentUserAccessor();
    }

    /// <summary>
    /// The environments the current user may see and control, or <c>null</c> when nothing restricts them: administrators, a user without
    /// the restriction, and system work (background jobs, the API with its key) which has no current user. A restricted user gets the
    /// ids granted to them (an empty set when none); an unknown user gets nothing. Read once per request.
    /// </summary>
    public async Task<IReadOnlySet<long>?> AllowedEnvironmentIdsAsync(CancellationToken ct = default)
    {
        if (_accessLoaded)
        {
            return _allowedIds;
        }

        if (_currentUser.UserId is not { } userId)
        {
            _allowedIds = null;
        }
        else
        {
            var user = await _db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Role, u.RestrictEnvironments })
                .FirstOrDefaultAsync(ct);
            _allowedIds = user is { Role: UserRole.Admin } or { RestrictEnvironments: false }
                ? null
                : user is null
                    ? new HashSet<long>() // not found: fail closed
                    : (await _db.UserEnvironments.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.EnvironmentId).ToListAsync(ct)).ToHashSet();
        }

        _accessLoaded = true;
        return _allowedIds;
    }

    public async Task<List<DockerEnvironment>> GetAllAsync(CancellationToken ct = default)
    {
        var allowed = await AllowedEnvironmentIdsAsync(ct);
        var query = _db.Environments.AsNoTracking();
        if (allowed is not null)
        {
            var ids = allowed.ToList();
            query = query.Where(e => ids.Contains(e.Id));
        }

        return await query.OrderBy(e => e.Name).ToListAsync(ct);
    }

    /// <summary>Only active environments — for operational lists (migration/restore targets, pickers, schedules).</summary>
    public async Task<List<DockerEnvironment>> GetEnabledAsync(CancellationToken ct = default)
    {
        var allowed = await AllowedEnvironmentIdsAsync(ct);
        var query = _db.Environments.AsNoTracking().Where(e => e.IsEnabled);
        if (allowed is not null)
        {
            var ids = allowed.ToList();
            query = query.Where(e => ids.Contains(e.Id));
        }

        return await query.OrderBy(e => e.Name).ToListAsync(ct);
    }

    /// <summary>One environment by id; <c>null</c> when it does not exist <em>or the current user may not see it</em> (a restricted user cannot reach one by its id).</summary>
    public async Task<DockerEnvironment?> GetAsync(long id, CancellationToken ct = default)
    {
        var allowed = await AllowedEnvironmentIdsAsync(ct);
        return allowed is not null && !allowed.Contains(id)
            ? null
            : await _db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    /// <summary>Activates or deactivates an environment. Returns false if it does not exist.</summary>
    public async Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        var entity = await _db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
        {
            return false;
        }

        entity.IsEnabled = enabled;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<DockerEnvironment> CreateAsync(EnvironmentInput input, CancellationToken ct = default)
    {
        var entity = new DockerEnvironment();
        ApplyInput(entity, input, isCreate: true);
        _db.Environments.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<bool> UpdateAsync(long id, EnvironmentInput input, CancellationToken ct = default)
    {
        var entity = await _db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
        {
            return false;
        }

        ApplyInput(entity, input, isCreate: false);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var entity = await _db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
        {
            return false;
        }

        _db.Environments.Remove(entity); // becomes a soft delete via the DbContext
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Builds SSH settings from a stored environment, decrypting its secrets.</summary>
    public SshConnectionSettings BuildSettings(DockerEnvironment env)
    {
        return new SshConnectionSettings
        {
            ConnectionType = env.ConnectionType,
            Host = env.Host,
            Port = env.Port,
            Username = env.Username,
            AuthType = env.AuthType,
            Password = _secrets.UnprotectNullable(env.EncryptedPassword),
            PrivateKeyPem = _secrets.UnprotectNullable(env.EncryptedPrivateKey),
            PrivateKeyPassphrase = _secrets.UnprotectNullable(env.EncryptedPrivateKeyPassphrase),
            TimeoutSeconds = _options.SshTimeoutSeconds,
            UseSudo = env.UseSudo,
            DockerHost = env.DockerHost,
            HostKeyFingerprint = env.ConnectionType == ConnectionType.Ssh ? env.HostKeyFingerprint : null,
            TlsCaPem = env.TlsCa,
            TlsCertPem = env.TlsCert,
            TlsKeyPem = _secrets.UnprotectNullable(env.EncryptedTlsKey),
        };
    }

    /// <summary>Builds SSH settings from unsaved form input (plaintext secrets, e.g. the Create page).</summary>
    public SshConnectionSettings BuildSettings(EnvironmentInput input)
    {
        return new SshConnectionSettings
        {
            ConnectionType = input.ConnectionType,
            Host = input.Host,
            Port = input.Port,
            Username = input.Username,
            AuthType = input.AuthType,
            Password = input.Password,
            PrivateKeyPem = input.PrivateKeyPem,
            PrivateKeyPassphrase = input.PrivateKeyPassphrase,
            TimeoutSeconds = _options.SshTimeoutSeconds
        };
    }

    /// <summary>
    /// Builds settings for an ad-hoc connection test from form input. When editing and the secret
    /// field was left blank, the stored (decrypted) secret is used instead, so the user does not have
    /// to re-enter it just to test.
    /// </summary>
    public async Task<SshConnectionSettings> BuildTestSettingsAsync(EnvironmentInput input, long? id, CancellationToken ct = default)
    {
        if (input.ConnectionType == ConnectionType.Local)
        {
            // Local needs no host/credentials; talk to the mounted socket.
            return new SshConnectionSettings
            {
                ConnectionType = ConnectionType.Local,
                Host = "local",
                Username = "local",
                TimeoutSeconds = _options.SshTimeoutSeconds,
            };
        }

        if (input.ConnectionType == ConnectionType.Tcp)
        {
            // A blank key keeps the stored one (it is never sent to the server; TLS only proves it).
            var storedKey = string.IsNullOrWhiteSpace(input.TlsKey) && id is > 0 && await GetAsync(id.Value, ct) is { } savedEnv
                ? _secrets.UnprotectNullable(savedEnv.EncryptedTlsKey)
                : null;
            return new SshConnectionSettings
            {
                ConnectionType = ConnectionType.Tcp,
                Host = (input.Host ?? string.Empty).Trim(),
                Port = input.Port,
                Username = string.Empty,
                TlsCaPem = Pem(input.TlsCa),
                TlsCertPem = Pem(input.TlsCert),
                TlsKeyPem = Pem(input.TlsKey) ?? storedKey,
                TimeoutSeconds = _options.SshTimeoutSeconds,
            };
        }

        var secretMissing = input.AuthType == AuthType.Password
            ? string.IsNullOrEmpty(input.Password)
            : string.IsNullOrEmpty(input.PrivateKeyPem);

        SshConnectionSettings? stored = null;
        if (secretMissing && id is > 0)
        {
            var entity = await GetAsync(id.Value, ct);
            // The stored secret only goes to the host it was saved for: a test against another host, port or user
            // must not hand the stored password or key to that host.
            if (entity is not null && SameEndpoint(entity, input))
            {
                stored = BuildSettings(entity);
            }
        }

        // The saved host key holds for the same endpoint only (a test of another host learns its key).
        string? hostKey = null;
        if (id is > 0 && (await GetAsync(id.Value, ct)) is { } saved && SameEndpoint(saved, input))
        {
            hostKey = saved.HostKeyFingerprint;
        }

        return new SshConnectionSettings
        {
            ConnectionType = ConnectionType.Ssh,
            Host = (input.Host ?? string.Empty).Trim(),
            Port = input.Port,
            Username = (input.Username ?? string.Empty).Trim(),
            AuthType = input.AuthType,
            Password = string.IsNullOrEmpty(input.Password) ? stored?.Password : input.Password,
            PrivateKeyPem = string.IsNullOrEmpty(input.PrivateKeyPem) ? stored?.PrivateKeyPem : input.PrivateKeyPem,
            PrivateKeyPassphrase = string.IsNullOrEmpty(input.PrivateKeyPassphrase) ? stored?.PrivateKeyPassphrase : input.PrivateKeyPassphrase,
            TimeoutSeconds = _options.SshTimeoutSeconds,
            HostKeyFingerprint = hostKey,
        };
    }

    /// <summary>Tests a stored environment and persists the resulting status.</summary>
    public async Task<DockerConnectionResult> TestAndPersistAsync(long id, CancellationToken ct = default)
    {
        var entity = await _db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
        {
            return DockerConnectionResult.Fail("Environment not found.");
        }

        DockerConnectionResult result;
        try
        {
            result = await _connection.TestConnectionAsync(BuildSettings(entity), ct);
        }
        catch (Exception ex)
        {
            // e.g. secrets can no longer be decrypted (Data Protection keys changed).
            result = DockerConnectionResult.Fail($"Credentials could not be read: {ex.Message}");
        }

        entity.Status = result.Status;
        entity.LastCheckedAt = DateTime.UtcNow;
        entity.LastCheckMessage = result.Message;
        entity.DockerVersion = result.DockerVersion;
        if (result.Success)
        {
            // Remember how Docker was reached, so volume/migration operations use the same access.
            entity.UseSudo = result.UseSudo;
            entity.DockerHost = result.DockerHost;
            entity.HostKeyFingerprint ??= result.HostKeyFingerprint;
        }
        await _db.SaveChangesAsync(ct);

        return result;
    }

    public async Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(DockerEnvironment env, CancellationToken ct = default)
        => await _connection.ListVolumesAsync(BuildSettings(env), ct);

    /// <summary>Name given to the Local environment that <see cref="EnsureLocalAsync"/> creates.</summary>
    public const string LocalEnvironmentName = "local";

    /// <summary>
    /// The environment that talks to this host's Docker socket: the first enabled Local environment (one named
    /// <c>local</c> first), else a disabled one; null when there is none.
    /// </summary>
    public async Task<DockerEnvironment?> FindLocalAsync(CancellationToken ct = default)
    {
        var locals = await _db.Environments
            .Where(e => e.ConnectionType == ConnectionType.Local)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        return locals
            .OrderByDescending(e => e.IsEnabled)
            .ThenByDescending(e => string.Equals(e.Name, LocalEnvironmentName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>
    /// Ensures an environment of type Local exists (idempotent): returns the existing one, or creates
    /// <c>local</c> after verifying that the Docker socket answers. Fails (nothing is created) when it does not.
    /// </summary>
    public async Task<(bool Ok, string Message, DockerEnvironment? Environment, bool Created)> EnsureLocalAsync(CancellationToken ct = default)
    {
        var existing = await FindLocalAsync(ct);
        if (existing is not null)
        {
            return (true, "A local environment already exists.", existing, false);
        }

        DockerConnectionResult probe;
        try
        {
            var settings = await BuildTestSettingsAsync(new EnvironmentInput { ConnectionType = ConnectionType.Local }, null, ct);
            probe = await _connection.TestConnectionAsync(settings, ct);
        }
        catch (Exception ex)
        {
            probe = DockerConnectionResult.Fail(ex.Message);
        }

        if (!probe.Success)
        {
            return (false, $"No Docker socket available: {probe.Message}", null, false);
        }

        var env = await CreateAsync(new EnvironmentInput
        {
            Name = LocalEnvironmentName,
            Description = "This host's Docker engine (mounted socket)",
            ConnectionType = ConnectionType.Local,
        }, ct);
        env.Status = probe.Status;
        env.LastCheckedAt = DateTime.UtcNow;
        env.LastCheckMessage = probe.Message;
        env.DockerVersion = probe.DockerVersion;
        await _db.SaveChangesAsync(ct);
        return (true, "Local environment created.", env, true);
    }

    /// <summary>Saves the host key seen at a successful connection when none is saved yet (trust on first use).</summary>
    public async Task RememberHostKeyAsync(long id, string? fingerprint, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return;
        }

        var entity = await _db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is { ConnectionType: ConnectionType.Ssh, HostKeyFingerprint: null })
        {
            entity.HostKeyFingerprint = fingerprint;
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Forgets the saved host key (the server was reinstalled); the next connection saves the new one.</summary>
    public async Task<bool> ForgetHostKeyAsync(long id, CancellationToken ct = default)
    {
        var entity = await _db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
        {
            return false;
        }

        entity.HostKeyFingerprint = null;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string? Pem(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Replace("\r\n", "\n").Trim() + "\n";

    /// <summary>Whether the input points at the same SSH endpoint (host, port, user) as the stored environment.</summary>
    public static bool SameEndpoint(DockerEnvironment entity, EnvironmentInput input)
        => entity.ConnectionType == ConnectionType.Ssh
           && string.Equals(entity.Host, (input.Host ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
           && entity.Port == input.Port
           && string.Equals(entity.Username, (input.Username ?? string.Empty).Trim(), StringComparison.Ordinal);

    private void ApplyInput(DockerEnvironment entity, EnvironmentInput input, bool isCreate)
    {
        entity.Name = input.Name.Trim();
        entity.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        entity.BaseUrl = string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim();
        entity.ConnectionType = input.ConnectionType;

        if (input.ConnectionType != ConnectionType.Tcp)
        {
            entity.TlsCa = null;
            entity.TlsCert = null;
            entity.EncryptedTlsKey = null;
        }

        if (input.ConnectionType == ConnectionType.Tcp)
        {
            // The Docker API of another host: address and port, the TLS certificates; no SSH secrets.
            entity.Host = input.Host.Trim();
            entity.Port = input.Port;
            entity.Username = string.Empty;
            entity.AuthType = AuthType.Password;
            entity.EncryptedPassword = null;
            entity.EncryptedPrivateKey = null;
            entity.EncryptedPrivateKeyPassphrase = null;
            entity.HostKeyFingerprint = null;
            entity.UseSudo = false;
            entity.DockerHost = null;
            entity.TlsCa = Pem(input.TlsCa);
            entity.TlsCert = Pem(input.TlsCert);
            if (Pem(input.TlsKey) is { } key)
            {
                entity.EncryptedTlsKey = _secrets.Protect(key);
            }
            else if (isCreate || entity.TlsCert is null)
            {
                entity.EncryptedTlsKey = null;
            }

            return;
        }

        if (input.ConnectionType == ConnectionType.Local)
        {
            // Local envs need no SSH host/credentials; use placeholders and clear any stored secrets.
            entity.Host = "local";
            entity.Username = "local";
            entity.Port = 0;
            entity.AuthType = AuthType.Password;
            entity.EncryptedPassword = null;
            entity.EncryptedPrivateKey = null;
            entity.EncryptedPrivateKeyPassphrase = null;
            // Drop any SSH-probe access hints so DockerHead doesn't emit sudo/remote-DOCKER_HOST locally
            // (re-detected on the next connection test).
            entity.UseSudo = false;
            entity.DockerHost = null;
            return;
        }

        // A secret belongs to its endpoint: when host, port or user change without a new secret, the stored one is
        // dropped instead of being sent to the new host.
        var endpointChanged = !isCreate && !SameEndpoint(entity, input);
        if (endpointChanged)
        {
            entity.HostKeyFingerprint = null; // another server: its key is learned at the next connection
        }
        entity.Host = input.Host.Trim();
        entity.Port = input.Port;
        entity.Username = input.Username.Trim();
        entity.AuthType = input.AuthType;

        if (input.AuthType == AuthType.Password)
        {
            // Password auth: keep only the password secret.
            entity.EncryptedPrivateKey = null;
            entity.EncryptedPrivateKeyPassphrase = null;
            if (!string.IsNullOrEmpty(input.Password))
            {
                entity.EncryptedPassword = _secrets.Protect(input.Password);
            }
            else if (isCreate || endpointChanged)
            {
                entity.EncryptedPassword = null;
            }
        }
        else
        {
            // Private-key auth: keep only the key secrets.
            entity.EncryptedPassword = null;
            if (!string.IsNullOrEmpty(input.PrivateKeyPem))
            {
                entity.EncryptedPrivateKey = _secrets.Protect(input.PrivateKeyPem);
            }
            else if (isCreate || endpointChanged)
            {
                entity.EncryptedPrivateKey = null;
            }

            // Passphrase may be intentionally empty; only overwrite on create or when a new key is supplied.
            if (!string.IsNullOrEmpty(input.PrivateKeyPassphrase))
            {
                entity.EncryptedPrivateKeyPassphrase = _secrets.Protect(input.PrivateKeyPassphrase);
            }
            else if (isCreate || endpointChanged || !string.IsNullOrEmpty(input.PrivateKeyPem))
            {
                entity.EncryptedPrivateKeyPassphrase = null;
            }
        }
    }
}
