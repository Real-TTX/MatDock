namespace MatDock.Core.Volumes;

public sealed class BackupResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long? BackupId { get; init; }

    /// <summary>Left out on purpose (its data lives elsewhere, see <see cref="Docker.DockerVolumeDetail.HoldsDataElsewhere"/>): neither a backup nor a failure.</summary>
    public bool Skipped { get; init; }

    public static BackupResult Skip(string message) => new() { Success = false, Skipped = true, Message = message };

    public static BackupResult Ok(long bytes, long backupId, string? warning = null) => new()
    {
        Success = true,
        BytesTransferred = bytes,
        BackupId = backupId,
        Message = warning is null
            ? $"Backup created - {VolumeMigrationResult.FormatBytes(bytes)}."
            : $"Backup created - {VolumeMigrationResult.FormatBytes(bytes)}. ⚠ {warning}"
    };

    public static BackupResult Fail(string message) => new() { Success = false, Message = message };
}

public sealed class RestoreResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }

    public static RestoreResult Ok(long bytes, string? warning = null) => new()
    {
        Success = true,
        BytesTransferred = bytes,
        Message = warning is null
            ? $"Restore successful - {VolumeMigrationResult.FormatBytes(bytes)} restored."
            : $"Restore successful - {VolumeMigrationResult.FormatBytes(bytes)} restored. ⚠ {warning}"
    };

    public static RestoreResult Fail(string message) => new() { Success = false, Message = message };
}
