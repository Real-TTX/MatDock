using System.Text.Json;

namespace MatDock.Core.Schedules;

/// <summary>Action-specific options for a scheduled task, persisted as JSON on the row.</summary>
public sealed class ScheduleOptions
{
    /// <summary>Images: prune ALL unused images, not just dangling ones.</summary>
    public bool All { get; set; }

    /// <summary>Volumes: also remove unused network shares (NFS/CIFS), not just local volumes.</summary>
    public bool IncludeShares { get; set; }

    // --- Backup action (self-contained) ---
    /// <summary>Volume names to back up, one per line.</summary>
    public string? VolumesCsv { get; set; }

    /// <summary>Backup target id, or null for local storage.</summary>
    public long? BackupTargetId { get; set; }

    /// <summary>Keep only the newest N archives per volume (0 = unlimited).</summary>
    public int RetentionCount { get; set; }

    /// <summary>Delete archives older than N days (0 = disabled).</summary>
    public int RetentionDays { get; set; }

    /// <summary>Stop the volume's containers during the backup for a consistent snapshot.</summary>
    public bool StopContainers { get; set; }

    // --- BundleBackup action (uses BackupTargetId, RetentionCount, RetentionDays and StopContainers too) ---
    /// <summary>The stack to back up; null = every managed stack of the task's owner (<c>ManagedBy</c>), or every
    /// managed stack when the task has no owner.</summary>
    public string? BundleStack { get; set; }

    // --- Sync action (reference) ---
    /// <summary>Sync action: the SyncJob (GitOps) to execute.</summary>
    public long? SyncJobId { get; set; }

    public static ScheduleOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ScheduleOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<ScheduleOptions>(json) ?? new ScheduleOptions();
        }
        catch (JsonException)
        {
            return new ScheduleOptions();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
