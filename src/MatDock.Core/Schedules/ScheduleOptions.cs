using System.Text.Json;

namespace MatDock.Core.Schedules;

/// <summary>Action-specific options for a scheduled task, persisted as JSON on the row.</summary>
public sealed class ScheduleOptions
{
    /// <summary>Images: prune ALL unused images, not just dangling ones.</summary>
    public bool All { get; set; }

    /// <summary>Volumes: also remove unused network shares (NFS/CIFS), not just local volumes.</summary>
    public bool IncludeShares { get; set; }

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
