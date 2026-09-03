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

    public async Task<(bool Success, string Message)> DeleteAsync(long id, CancellationToken ct = default)
    {
        var target = await _db.BackupTargets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (target is null)
        {
            return (false, "Target not found.");
        }

        // Do not orphan backups: a target that still holds archives must not be removed, otherwise those
        // backups become unrestorable (the historical target row would be gone).
        var referencing = await _db.VolumeBackups.CountAsync(b => b.BackupTargetId == id, ct);
        if (referencing > 0)
        {
            return (false, $"Target is still used by {referencing} backup(s) - delete those first.");
        }

        _db.BackupTargets.Remove(target);
        await _db.SaveChangesAsync(ct);
        return (true, "Target deleted.");
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
                ? $"Connected to \\\\{target.SmbHost}\\{target.SmbShare}."
                : "Local storage is writable.");
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

        try
        {
            var password = input.SmbPassword;
            if (string.IsNullOrEmpty(password) && existingId is { } id)
            {
                var stored = await GetAsync(id, ct);
                password = stored is null ? null : _secrets.UnprotectNullable(stored.EncryptedSmbPassword);
            }

            probe.EncryptedSmbPassword = string.IsNullOrEmpty(password) ? null : _secrets.Protect(password);
        }
        catch (Exception ex)
        {
            return (false, $"Credentials could not be read: {ex.Message}");
        }

        return await TestAsync(probe, ct);
    }

    /// <summary>File extension that marks a full stack/container bundle manifest at a target.</summary>
    public const string BundleExtension = ".mdbundle.json";

    /// <summary>An archive physically present at a target, enriched with DB metadata when known.</summary>
    public sealed record TargetArchive(
        string FileName, long SizeBytes, DateTime? ModifiedUtc, bool Known,
        string? VolumeName, string? SourceEnvName, bool IsBundle);

    /// <summary>
    /// Lists the archives physically present at a target (null = local), merged with this instance's DB
    /// records so foreign/older backups written to the same target are also shown and can be restored.
    /// </summary>
    public async Task<(IReadOnlyList<TargetArchive> Archives, string? Error)> ListArchivesAsync(BackupTarget? target, CancellationToken ct = default)
    {
        IReadOnlyList<BackupFileInfo> files;
        try
        {
            files = await _storageFactory.Create(target).ListAsync(ct);
        }
        catch (Exception ex)
        {
            return (Array.Empty<TargetArchive>(), ex.Message);
        }

        var targetId = target?.Id;
        var records = await _db.VolumeBackups.AsNoTracking().Where(b => b.BackupTargetId == targetId).ToListAsync(ct);
        var byName = new Dictionary<string, VolumeBackup>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            byName[r.FileName] = r;
        }

        var archives = files.Select(f =>
        {
            var isBundle = f.FileName.EndsWith(BundleExtension, StringComparison.OrdinalIgnoreCase);
            byName.TryGetValue(f.FileName, out var rec);
            var volume = rec?.VolumeName;
            if (volume is null && !isBundle && Volumes.VolumeBackupService.TryParseArchiveName(f.FileName, out _, out var parsedVol, out _))
            {
                volume = parsedVol;
            }

            return new TargetArchive(f.FileName, f.SizeBytes, f.ModifiedUtc, rec is not null, volume, rec?.SourceEnvironmentName, isBundle);
        })
        .OrderByDescending(a => a.ModifiedUtc ?? DateTime.MinValue)
        .ToList();

        return (archives, null);
    }

    public Task<Stream> OpenArchiveAsync(BackupTarget? target, string fileName, CancellationToken ct = default)
        => _storageFactory.Create(target).OpenReadAsync(Path.GetFileName(fileName), ct);

    public async Task DeleteArchiveAsync(BackupTarget? target, string fileName, CancellationToken ct = default)
    {
        var safe = Path.GetFileName(fileName);
        await _storageFactory.Create(target).DeleteAsync(safe, ct);

        // Keep History consistent: drop any DB record that referenced this file at this target.
        var targetId = target?.Id;
        var rec = await _db.VolumeBackups.FirstOrDefaultAsync(b => b.BackupTargetId == targetId && b.FileName == safe, ct);
        if (rec is not null)
        {
            _db.VolumeBackups.Remove(rec);
            await _db.SaveChangesAsync(ct);
        }
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
