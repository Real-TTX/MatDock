using MatDock.Core.Ssh;

namespace MatDock.Core.Volumes;

/// <summary>How a volume migration transfers the data.</summary>
public enum MigrationMode
{
    /// <summary>tar streamed straight from source into target over SSH — fast, no intermediate storage.</summary>
    Direct = 0,

    /// <summary>Back the source up to a backup target, then restore into the target — leaves a safety-net backup.</summary>
    ViaBackup = 1,
}

/// <summary>Everything needed to move one volume from a source host to a target host.</summary>
public sealed class VolumeMigrationRequest
{
    public required SshConnectionSettings Source { get; init; }

    public required string SourceVolume { get; init; }

    public required SshConnectionSettings Target { get; init; }

    public required string TargetVolume { get; init; }

    /// <summary>When false, the migration aborts if the target volume already contains data.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Stop the source volume's containers during the transfer (consistency), then restart them.</summary>
    public bool StopContainers { get; init; }
}

/// <summary>Outcome of a volume migration.</summary>
public sealed class VolumeMigrationResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public long BytesTransferred { get; init; }

    public double DurationSeconds { get; init; }

    public List<string> Steps { get; init; } = new();

    public static VolumeMigrationResult Ok(long bytes, double seconds, List<string> steps) => new()
    {
        Success = true,
        BytesTransferred = bytes,
        DurationSeconds = seconds,
        Steps = steps,
        Message = $"Migration erfolgreich – {FormatBytes(bytes)} übertragen."
    };

    public static VolumeMigrationResult Fail(string message, List<string>? steps = null) => new()
    {
        Success = false,
        Message = message,
        Steps = steps ?? new List<string>()
    };

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
