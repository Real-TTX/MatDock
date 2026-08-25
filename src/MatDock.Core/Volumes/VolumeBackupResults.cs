namespace MatDock.Core.Volumes;

public sealed class BackupResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long? BackupId { get; init; }

    public static BackupResult Ok(long bytes, long backupId) => new()
    {
        Success = true,
        BytesTransferred = bytes,
        BackupId = backupId,
        Message = $"Backup erstellt – {VolumeMigrationResult.FormatBytes(bytes)}."
    };

    public static BackupResult Fail(string message) => new() { Success = false, Message = message };
}

public sealed class RestoreResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }

    public static RestoreResult Ok(long bytes) => new()
    {
        Success = true,
        BytesTransferred = bytes,
        Message = $"Restore erfolgreich – {VolumeMigrationResult.FormatBytes(bytes)} zurückgespielt."
    };

    public static RestoreResult Fail(string message) => new() { Success = false, Message = message };
}
