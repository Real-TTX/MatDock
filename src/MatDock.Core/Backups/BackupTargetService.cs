using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Backups;

/// <summary>CRUD + connectivity test for backup targets (NAS/SMB). "Local" is the implicit fallback.</summary>
public sealed class BackupTargetService
{
    private readonly MatDockDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IBackupStorageFactory _storageFactory;

    public BackupTargetService(MatDockDbContext db, ISecretProtector secrets, IBackupStorageFactory storageFactory)
    {
        _db = db;
        _secrets = secrets;
        _storageFactory = storageFactory;
    }

    public Task<List<BackupTarget>> GetAllAsync(CancellationToken ct = default)
        => _db.BackupTargets.AsNoTracking().OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name).ToListAsync(ct);

    public Task<BackupTarget?> GetAsync(long id, CancellationToken ct = default)
        => _db.BackupTargets.FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>The default target row, or <c>null</c> which means local storage.</summary>
    public Task<BackupTarget?> GetDefaultAsync(CancellationToken ct = default)
        => _db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.IsDefault, ct);

    public async Task<BackupTarget> CreateAsync(BackupTargetInput input, CancellationToken ct = default)
    {
        var target = new BackupTarget();
        ApplyInput(target, input, isCreate: true);
        _db.BackupTargets.Add(target);
        await _db.SaveChangesAsync(ct);
        if (target.IsDefault)
        {
            await ClearOtherDefaultsAsync(target.Id, ct);
        }

        return target;
    }

    public async Task<bool> UpdateAsync(long id, BackupTargetInput input, CancellationToken ct = default)
    {
        var target = await _db.BackupTargets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (target is null)
        {
            return false;
        }

        ApplyInput(target, input, isCreate: false);
        await _db.SaveChangesAsync(ct);
        if (target.IsDefault)
        {
            await ClearOtherDefaultsAsync(target.Id, ct);
        }

        return true;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var target = await _db.BackupTargets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (target is null)
        {
            return false;
        }

        _db.BackupTargets.Remove(target);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Marks a target as default (<paramref name="id"/> null = local is default, clears all).</summary>
    public async Task SetDefaultAsync(long? id, CancellationToken ct = default)
    {
        var all = await _db.BackupTargets.ToListAsync(ct);
        foreach (var t in all)
        {
            t.IsDefault = id.HasValue && t.Id == id.Value;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<(bool Success, string Message)> TestAsync(BackupTarget target, CancellationToken ct = default)
    {
        try
        {
            await _storageFactory.Create(target).TestAsync(ct);
            return (true, target.Type == BackupTargetType.Smb
                ? $"Verbunden mit \\\\{target.SmbHost}\\{target.SmbShare}."
                : "Lokaler Speicher ist beschreibbar.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Tests unsaved form input; falls back to the stored password when left blank on edit.</summary>
    public async Task<(bool Success, string Message)> TestInputAsync(BackupTargetInput input, long? existingId, CancellationToken ct = default)
    {
        var probe = new BackupTarget
        {
            Name = input.Name,
            Type = input.Type,
            SmbHost = input.SmbHost,
            SmbShare = input.SmbShare,
            SmbDirectory = input.SmbDirectory,
            SmbUsername = input.SmbUsername,
            SmbDomain = input.SmbDomain
        };

        var password = input.SmbPassword;
        if (string.IsNullOrEmpty(password) && existingId is { } id)
        {
            var stored = await GetAsync(id, ct);
            password = stored is null ? null : _secrets.UnprotectNullable(stored.EncryptedSmbPassword);
        }

        probe.EncryptedSmbPassword = string.IsNullOrEmpty(password) ? null : _secrets.Protect(password);
        return await TestAsync(probe, ct);
    }

    private async Task ClearOtherDefaultsAsync(long keepId, CancellationToken ct)
    {
        var others = await _db.BackupTargets.Where(t => t.IsDefault && t.Id != keepId).ToListAsync(ct);
        if (others.Count == 0)
        {
            return;
        }

        foreach (var other in others)
        {
            other.IsDefault = false;
        }

        await _db.SaveChangesAsync(ct);
    }

    private void ApplyInput(BackupTarget target, BackupTargetInput input, bool isCreate)
    {
        target.Name = input.Name.Trim();
        target.Type = input.Type;
        target.IsDefault = input.IsDefault;

        if (input.Type == BackupTargetType.Smb)
        {
            target.SmbHost = input.SmbHost?.Trim();
            target.SmbShare = input.SmbShare?.Trim();
            target.SmbDirectory = string.IsNullOrWhiteSpace(input.SmbDirectory) ? null : input.SmbDirectory.Trim();
            target.SmbUsername = input.SmbUsername?.Trim();
            target.SmbDomain = string.IsNullOrWhiteSpace(input.SmbDomain) ? null : input.SmbDomain.Trim();
            if (!string.IsNullOrEmpty(input.SmbPassword))
            {
                target.EncryptedSmbPassword = _secrets.Protect(input.SmbPassword);
            }
            else if (isCreate)
            {
                target.EncryptedSmbPassword = null;
            }
        }
        else
        {
            target.SmbHost = target.SmbShare = target.SmbDirectory = target.SmbUsername = target.SmbDomain = null;
            target.EncryptedSmbPassword = null;
        }
    }
}
