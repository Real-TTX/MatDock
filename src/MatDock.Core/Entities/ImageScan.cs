namespace MatDock.Core.Entities;

/// <summary>The last vulnerability scan of an image on an environment (Trivy): the counts per severity and the most severe findings.</summary>
public class ImageScan : AuditableEntity
{
    public long EnvironmentId { get; set; }

    /// <summary>The image as it was scanned (repository:tag, or an id).</summary>
    public string ImageRef { get; set; } = string.Empty;

    public DateTime ScannedAt { get; set; }

    public int Critical { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    public int Unknown { get; set; }

    /// <summary>Why the last scan failed (the counts are then those of the scan before), or null.</summary>
    public string? Error { get; set; }

    /// <summary>The most severe findings as JSON (<see cref="Images.ImageFinding"/>).</summary>
    public string? FindingsJson { get; set; }
}
