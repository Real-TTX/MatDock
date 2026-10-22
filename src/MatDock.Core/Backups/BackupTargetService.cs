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

    /// <summary>
    /// What is wrong with the input of a target, or null. <paramref name="hasStoredResticPassword"/>: an edit may leave the password blank to keep
    /// the stored one (the same goes for the keys, which are optional anyway).
    /// </summary>
    public static string? ValidateInput(BackupTargetInput input, bool hasStoredResticPassword = false)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Please enter a name.";
        }

        switch (input.Type)
        {
            case BackupTargetType.Smb:
                if (string.IsNullOrWhiteSpace(input.SmbHost)) { return "Please enter the NAS host."; }
                if (string.IsNullOrWhiteSpace(input.SmbShare)) { return "Please enter the share."; }
                return null;

            case BackupTargetType.Restic:
                if (ResticSettings.ValidateRepository(input.ResticRepository) is { } repoProblem) { return repoProblem; }
                if (string.IsNullOrEmpty(input.ResticPassword) && !hasStoredResticPassword) { return "Enter the repository password: restic encrypts the backups with it. Keep it safe, without it they cannot be restored."; }
                return ResticSettings.ParseEnv(input.ResticEnv).Error;

            default:
                return null;
        }
    }

    public async Task<BackupTarget> CreateAsync(BackupTargetInput input, CancellationToken ct = default)
    {
        if (ValidateInput(input) is { } problem)
        {
            throw new ArgumentException(problem, nameof(input));
        }

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

        if (input.Type != target.Type)
        {
            // Its backups were written the old way; reading them the new way would find nothing.
            throw new ArgumentException("The type of a target cannot be changed; create a new target.", nameof(input));
        }

        if (ValidateInput(input, hasStoredResticPassword: target.EncryptedResticPassword is not null) is { } problem)
        {
            throw new ArgumentException(problem, nameof(input));
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
            return (true, target.Type switch
            {
                BackupTargetType.Smb => $"Connected to \\\\{target.SmbHost}\\{target.SmbShare}.",
                BackupTargetType.Restic => "The restic repository is reachable and the password is right.",
                _ => "Local storage is writable.",
            });
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
            SmbDomain = input.SmbDomain,
            ResticRepository = input.ResticRepository?.Trim(),
        };

        try
        {
            var stored = existingId is { } id ? await GetAsync(id, ct) : null;

            var password = input.SmbPassword;
            if (string.IsNullOrEmpty(password) && stored is not null)
            {
                password = _secrets.UnprotectNullable(stored.EncryptedSmbPassword);
            }

            probe.EncryptedSmbPassword = string.IsNullOrEmpty(password) ? null : _secrets.Protect(password);

            // restic: what is typed wins; blank fields use the stored password and keys (the form never shows them).
            var resticPassword = string.IsNullOrEmpty(input.ResticPassword) && stored is not null
                ? _secrets.UnprotectNullable(stored.EncryptedResticPassword)
                : input.ResticPassword;
            probe.EncryptedResticPassword = string.IsNullOrEmpty(resticPassword) ? null : _secrets.Protect(resticPassword);

            var resticEnv = string.IsNullOrWhiteSpace(input.ResticEnv) && stored is not null
                ? _secrets.UnprotectNullable(stored.EncryptedResticEnv)
                : input.ResticEnv;
            probe.EncryptedResticEnv = string.IsNullOrWhiteSpace(resticEnv) ? null : _secrets.Protect(resticEnv);
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

        // Stack bundles are recorded as stack backups (their manifest), not as volume backups.
        var bundles = (await _db.StackBackups.AsNoTracking().Where(b => b.BackupTargetId == targetId && b.ManifestFileName != null).ToListAsync(ct))
            .GroupBy(b => b.ManifestFileName!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var archives = files.Select(f =>
        {
            var isBundle = f.FileName.EndsWith(BundleExtension, StringComparison.OrdinalIgnoreCase);
            byName.TryGetValue(f.FileName, out var rec);
            var volume = rec?.VolumeName;
            if (volume is null && !isBundle && Volumes.VolumeBackupService.TryParseArchiveName(f.FileName, out _, out var parsedVol, out _))
            {
                volume = parsedVol;
            }

            if (isBundle && bundles.TryGetValue(f.FileName, out var bundle))
            {
                return new TargetArchive(f.FileName, f.SizeBytes, f.ModifiedUtc, true, bundle.StackName, bundle.EnvironmentName, true);
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

        if (input.Type == BackupTargetType.Restic)
        {
            target.ResticRepository = input.ResticRepository?.Trim();
            // Blank keeps what is stored (the form never shows a password or key back).
            if (!string.IsNullOrEmpty(input.ResticPassword))
            {
                target.EncryptedResticPassword = _secrets.Protect(input.ResticPassword);
            }
            if (!string.IsNullOrWhiteSpace(input.ResticEnv))
            {
                target.EncryptedResticEnv = _secrets.Protect(input.ResticEnv);
            }
            else if (isCreate)
            {
                target.EncryptedResticEnv = null;
            }
        }
        else
        {
            target.ResticRepository = null;
            target.EncryptedResticPassword = null;
            target.EncryptedResticEnv = null;
        }
    }
}
