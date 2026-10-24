using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Backups;

/// <summary>
/// The file format of a backup of MatDock itself: <c>MDSB1</c>, a salt, a nonce, then a tar (the database and the key ring) encrypted with AES-256-GCM. The key
/// comes from a passphrase (PBKDF2-SHA256, 600 000 rounds): the archive holds the keys that open every stored password, so it is never written unencrypted.
/// </summary>
public static class SelfBackupFormat
{
    private static readonly byte[] Magic = "MDSB1"u8.ToArray();
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Rounds = 600_000;

    public const int MinPassphraseLength = 12;

    public static byte[] Encrypt(byte[] plain, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Rounds, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Magic);
        }

        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    /// <summary>The plain archive; throws <see cref="CryptographicException"/> for a wrong passphrase or a damaged file, <see cref="InvalidDataException"/> for something else.</summary>
    public static byte[] Decrypt(byte[] data, string passphrase)
    {
        var header = Magic.Length + SaltSize + NonceSize + TagSize;
        if (data.Length < header || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("This is no backup of MatDock.");
        }

        var salt = data.AsSpan(Magic.Length, SaltSize);
        var nonce = data.AsSpan(Magic.Length + SaltSize, NonceSize);
        var tag = data.AsSpan(Magic.Length + SaltSize + NonceSize, TagSize);
        var cipher = data.AsSpan(header);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Rounds, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, Magic);
        return plain;
    }
}

/// <summary>
/// A backup of MatDock itself: its database (environments with their access, stacks, schedules, rules, users) and the key ring that decrypts the stored
/// passwords, encrypted with a passphrase (<see cref="SelfBackupFormat"/>). Restoring it takes effect at the next start (<see cref="SelfBackupRestore"/>).
/// The backups of volumes and stacks are not in it: they live at their targets.
/// </summary>
public sealed class SelfBackupService
{
    /// <summary>The names the files of a backup at a target begin and end with.</summary>
    public const string FilePrefix = "matdock-selfbackup-";
    public const string FileExtension = ".mdsb";

    private readonly MatDockDbContext _db;
    private readonly AppPaths _paths;
    private readonly IBackupStorageFactory _storages;
    private readonly ILogger<SelfBackupService> _logger;

    public SelfBackupService(MatDockDbContext db, AppPaths paths, IBackupStorageFactory storages, ILogger<SelfBackupService> logger)
    {
        _db = db;
        _paths = paths;
        _storages = storages;
        _logger = logger;
    }

    public static string? PassphraseProblem(string? passphrase)
        => passphrase is null || passphrase.Length < SelfBackupFormat.MinPassphraseLength
            ? $"The passphrase has at least {SelfBackupFormat.MinPassphraseLength} characters (it is the only way to open the backup)."
            : null;

    public static string FileName(DateTime utc) => $"{FilePrefix}{utc:yyyyMMdd-HHmmss}{FileExtension}";

    /// <summary>The encrypted backup: a consistent copy of the database (<c>VACUUM INTO</c>, while MatDock runs) and the key ring.</summary>
    public async Task<byte[]> CreateAsync(string passphrase, CancellationToken ct = default)
    {
        if (PassphraseProblem(passphrase) is { } problem)
        {
            throw new ArgumentException(problem, nameof(passphrase));
        }

        var copy = Path.Combine(Path.GetTempPath(), $"matdock-selfbackup-{Guid.NewGuid():N}.db");
        try
        {
#pragma warning disable EF1002 // the path is ours (a temporary file), quoted for SQLite
            await _db.Database.ExecuteSqlRawAsync("VACUUM INTO '" + copy.Replace("'", "''") + "'", ct);
#pragma warning restore EF1002

            using var tar = new MemoryStream();
            await using (var writer = new TarWriter(tar, leaveOpen: true))
            {
                var manifest = JsonSerializer.SerializeToUtf8Bytes(new { app = "matdock", version = AppVersion.Informational, createdUtc = DateTime.UtcNow });
                await AddAsync(writer, "manifest.json", manifest, ct);
                await AddAsync(writer, "matdock.db", await File.ReadAllBytesAsync(copy, ct), ct);
                foreach (var key in Directory.Exists(_paths.KeysPath) ? Directory.GetFiles(_paths.KeysPath) : [])
                {
                    await AddAsync(writer, "keys/" + Path.GetFileName(key), await File.ReadAllBytesAsync(key, ct), ct);
                }
            }

            return SelfBackupFormat.Encrypt(tar.ToArray(), passphrase);
        }
        finally
        {
            TryDelete(copy);
        }
    }

    /// <summary>
    /// Checks a backup (the passphrase, a database in it) and puts it aside for the next start, which swaps it in. Answers what it found or why it refused.
    /// Nothing of the running MatDock changes until then.
    /// </summary>
    public async Task<(bool Ok, string Message)> PrepareRestoreAsync(Stream archive, string passphrase, CancellationToken ct = default)
    {
        byte[] plain;
        try
        {
            using var buffer = new MemoryStream();
            await archive.CopyToAsync(buffer, ct);
            plain = SelfBackupFormat.Decrypt(buffer.ToArray(), passphrase);
        }
        catch (InvalidDataException ex)
        {
            return (false, ex.Message);
        }
        catch (CryptographicException)
        {
            return (false, "The passphrase does not open this backup (or the file is damaged).");
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var reader = new TarReader(new MemoryStream(plain));
            while (await reader.GetNextEntryAsync(copyData: true, ct) is { } entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data)
                {
                    using var content = new MemoryStream();
                    await data.CopyToAsync(content, ct);
                    files[entry.Name] = content.ToArray();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            return (false, "The backup could not be read.");
        }

        if (!files.TryGetValue("matdock.db", out var database) || database.Length < 16 || !database.AsSpan(0, 15).SequenceEqual("SQLite format 3"u8))
        {
            return (false, "The backup holds no database of MatDock.");
        }

        var keys = files.Where(f => f.Key.StartsWith("keys/", StringComparison.Ordinal) && SafeName(f.Key[5..])).ToList();
        var pending = SelfBackupRestore.PendingPath(_paths);
        TryDeleteDirectory(pending);
        Directory.CreateDirectory(Path.Combine(pending, "keys"));
        await File.WriteAllBytesAsync(Path.Combine(pending, "matdock.db"), database, ct);
        foreach (var (name, content) in keys)
        {
            await File.WriteAllBytesAsync(Path.Combine(pending, "keys", name[5..]), content, ct);
        }

        var made = files.TryGetValue("manifest.json", out var manifest) && JsonDocument.Parse(manifest).RootElement.TryGetProperty("createdUtc", out var created)
            ? created.GetDateTime().ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "an unknown time";
        return (true, $"The backup of {made} ({keys.Count} key(s)) is ready: it takes the place of the current data at the next start of MatDock.");
    }

    public bool HasPendingRestore => Directory.Exists(SelfBackupRestore.PendingPath(_paths));

    /// <summary>Drops a restore that was prepared and not applied yet.</summary>
    public void CancelPendingRestore() => TryDeleteDirectory(SelfBackupRestore.PendingPath(_paths));

    /// <summary>Writes a backup to a target (null = the local backup folder) and keeps the newest <paramref name="keep"/> there (0 = all).</summary>
    public async Task<(bool Ok, string Summary)> BackupToTargetAsync(BackupTarget? target, string passphrase, int keep, CancellationToken ct = default)
    {
        var storage = _storages.Create(target);
        var name = FileName(DateTime.UtcNow);
        var data = await CreateAsync(passphrase, ct);
        await using (var output = await storage.OpenWriteAsync(name, ct))
        {
            await output.WriteAsync(data, ct);
        }

        var removed = 0;
        if (keep > 0)
        {
            var old = (await storage.ListAsync(ct))
                .Where(f => f.FileName.StartsWith(FilePrefix, StringComparison.Ordinal) && f.FileName.EndsWith(FileExtension, StringComparison.Ordinal))
                .OrderByDescending(f => f.FileName, StringComparer.Ordinal)
                .Skip(keep)
                .ToList();
            foreach (var file in old)
            {
                try
                {
                    await storage.DeleteAsync(file.FileName, ct);
                    removed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogInformation(ex, "Removing the old backup {File} failed.", file.FileName);
                }
            }
        }

        return (true, $"{name} ({data.Length / 1024} KiB) written to {target?.Name ?? "the local backup folder"}{(removed > 0 ? $"; {removed} older one(s) removed" : string.Empty)}.");
    }

    private static async Task AddAsync(TarWriter writer, string name, byte[] content, CancellationToken ct)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) };
        await writer.WriteEntryAsync(entry, ct);
    }

    private static bool SafeName(string name) => name.Length > 0 && name.IndexOfAny(['/', '\\']) < 0 && name is not ("." or "..");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Swaps a prepared restore in at the start, before anything opens the database or reads the keys. The data it replaces is kept beside it.</summary>
public static class SelfBackupRestore
{
    public static string PendingPath(AppPaths paths) => Path.Combine(paths.DataPath, "restore-pending");

    /// <summary>Applies a pending restore; answers what it did (null when there was none).</summary>
    public static string? ApplyPending(AppPaths paths)
    {
        var pending = PendingPath(paths);
        var database = Path.Combine(pending, "matdock.db");
        if (!File.Exists(database))
        {
            return null;
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var keep = Path.Combine(paths.DataPath, $"before-restore-{stamp}");
        Directory.CreateDirectory(keep);

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var current = paths.DatabasePath + suffix;
            if (File.Exists(current))
            {
                File.Move(current, Path.Combine(keep, Path.GetFileName(current)));
            }
        }

        File.Move(database, paths.DatabasePath);

        var keys = Path.Combine(pending, "keys");
        if (Directory.Exists(keys) && Directory.GetFiles(keys).Length > 0)
        {
            if (Directory.Exists(paths.KeysPath))
            {
                Directory.Move(paths.KeysPath, Path.Combine(keep, Path.GetFileName(paths.KeysPath)));
            }

            Directory.Move(keys, paths.KeysPath);
        }

        Directory.Delete(pending, recursive: true);
        return $"Restored MatDock from a backup; the data it replaced is in {keep}.";
    }
}
