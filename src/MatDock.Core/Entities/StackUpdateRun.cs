namespace MatDock.Core.Entities;

/// <summary>How a stack update run ended.</summary>
public enum UpdateRunOutcome
{
    /// <summary>At least one container was re-created with a new image.</summary>
    Updated = 0,

    /// <summary>The pull found nothing new; no container changed.</summary>
    NoChange = 1,

    /// <summary>The update (or the backup before it) failed; nothing was rolled back.</summary>
    Failed = 2,

    /// <summary>The new containers were not healthy and the old images were put back.</summary>
    RolledBack = 3,
}

/// <summary>
/// One update of a managed stack: when, who started it (a person, a schedule, the API), how it ended and which services changed
/// image. Kept per stack (the newest <see cref="Updates.UpdateService.KeepRuns"/>), so an update that went wrong can be traced.
/// </summary>
public class StackUpdateRun : AuditableEntity
{
    public long StackId { get; set; }

    /// <summary>The stack's name at the time (it stays readable after the stack is deleted).</summary>
    public string StackName { get; set; } = string.Empty;

    public long EnvironmentId { get; set; }

    /// <summary>"manual", "schedule" or "api".</summary>
    public string Trigger { get; set; } = "manual";

    public UpdateRunOutcome Outcome { get; set; }

    /// <summary>The first lines of what the update said, shortened.</summary>
    public string? Message { get; set; }

    /// <summary>JSON array of <c>{service, image, before, after}</c> (image ids) for the services whose image changed.</summary>
    public string? ChangesJson { get; set; }
}
