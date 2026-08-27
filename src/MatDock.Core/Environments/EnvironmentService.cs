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

    public EnvironmentService(
        MatDockDbContext db,
        ISecretProtector secrets,
        IEnvironmentConnectionService connection,
        IOptions<MatDockOptions> options)
    {
        _db = db;
        _secrets = secrets;
        _connection = connection;
        _options = options.Value;
    }

    public Task<List<DockerEnvironment>> GetAllAsync(CancellationToken ct = default)
        => _db.Environments.AsNoTracking().OrderBy(e => e.Name).ToListAsync(ct);

    /// <summary>Only active environments — for operational lists (migration/restore targets, pickers, schedules).</summary>
    public Task<List<DockerEnvironment>> GetEnabledAsync(CancellationToken ct = default)
        => _db.Environments.AsNoTracking().Where(e => e.IsEnabled).OrderBy(e => e.Name).ToListAsync(ct);

    public Task<DockerEnvironment?> GetAsync(long id, CancellationToken ct = default)
        => _db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);

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
            Host = env.Host,
            Port = env.Port,
            Username = env.Username,
            AuthType = env.AuthType,
            Password = _secrets.UnprotectNullable(env.EncryptedPassword),
            PrivateKeyPem = _secrets.UnprotectNullable(env.EncryptedPrivateKey),
            PrivateKeyPassphrase = _secrets.UnprotectNullable(env.EncryptedPrivateKeyPassphrase),
            TimeoutSeconds = _options.SshTimeoutSeconds,
            UseSudo = env.UseSudo,
            DockerHost = env.DockerHost
        };
    }

    /// <summary>Builds SSH settings from unsaved form input (plaintext secrets, e.g. the Create page).</summary>
    public SshConnectionSettings BuildSettings(EnvironmentInput input)
    {
        return new SshConnectionSettings
        {
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
        var secretMissing = input.AuthType == AuthType.Password
            ? string.IsNullOrEmpty(input.Password)
            : string.IsNullOrEmpty(input.PrivateKeyPem);

        SshConnectionSettings? stored = null;
        if (secretMissing && id is > 0)
        {
            var entity = await GetAsync(id.Value, ct);
            if (entity is not null)
            {
                stored = BuildSettings(entity);
            }
        }

        return new SshConnectionSettings
        {
            Host = input.Host.Trim(),
            Port = input.Port,
            Username = input.Username.Trim(),
            AuthType = input.AuthType,
            Password = string.IsNullOrEmpty(input.Password) ? stored?.Password : input.Password,
            PrivateKeyPem = string.IsNullOrEmpty(input.PrivateKeyPem) ? stored?.PrivateKeyPem : input.PrivateKeyPem,
            PrivateKeyPassphrase = string.IsNullOrEmpty(input.PrivateKeyPassphrase) ? stored?.PrivateKeyPassphrase : input.PrivateKeyPassphrase,
            TimeoutSeconds = _options.SshTimeoutSeconds
        };
    }

    /// <summary>Tests a stored environment and persists the resulting status.</summary>
    public async Task<DockerConnectionResult> TestAndPersistAsync(long id, CancellationToken ct = default)
    {
        var entity = await _db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
        {
            return DockerConnectionResult.Fail("Environment nicht gefunden.");
        }

        DockerConnectionResult result;
        try
        {
            result = await _connection.TestConnectionAsync(BuildSettings(entity), ct);
        }
        catch (Exception ex)
        {
            // e.g. secrets can no longer be decrypted (Data Protection keys changed).
            result = DockerConnectionResult.Fail($"Zugangsdaten konnten nicht gelesen werden: {ex.Message}");
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
        }
        await _db.SaveChangesAsync(ct);

        return result;
    }

    public async Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(DockerEnvironment env, CancellationToken ct = default)
        => await _connection.ListVolumesAsync(BuildSettings(env), ct);

    private void ApplyInput(DockerEnvironment entity, EnvironmentInput input, bool isCreate)
    {
        entity.Name = input.Name.Trim();
        entity.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        entity.BaseUrl = string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim();
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
            else if (isCreate)
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
            else if (isCreate)
            {
                entity.EncryptedPrivateKey = null;
            }

            // Passphrase may be intentionally empty; only overwrite on create or when a new key is supplied.
            if (!string.IsNullOrEmpty(input.PrivateKeyPassphrase))
            {
                entity.EncryptedPrivateKeyPassphrase = _secrets.Protect(input.PrivateKeyPassphrase);
            }
            else if (isCreate || !string.IsNullOrEmpty(input.PrivateKeyPem))
            {
                entity.EncryptedPrivateKeyPassphrase = null;
            }
        }
    }
}
