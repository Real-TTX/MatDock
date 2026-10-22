namespace MatDock.Core.Updates;

/// <summary>Update status of one image used by a stack: local vs. registry digest.</summary>
/// <param name="Pinned">The service is pinned: updates skip it.</param>
/// <param name="NewerTag">A newer version tag in the registry (<c>1.2.3</c> → <c>1.3.0</c>); only a hint, nothing changes by itself.</param>
public sealed record ImageUpdateStatus(
    string Service,
    string Image,
    string? LocalDigest,
    string? RegistryDigest,
    bool UpdateAvailable,
    string? Note,
    bool Pinned = false,
    string? NewerTag = null);

/// <summary>Result of an update run: success, the compose output and whether any container was re-created.</summary>
/// <param name="RolledBack">The new containers were not healthy and the old images were put back.</param>
/// <param name="NotStarted">It failed before anything was changed (the stack or environment was not there, or the backup before it failed).</param>
/// <param name="Deployed">It failed, but only after the new containers were up (they are not healthy and no rollback was asked for): the stack runs what the update brought.</param>
public sealed record StackUpdateOutcome(bool Ok, string Message, bool Changed, bool RolledBack = false, bool NotStarted = false, bool Deployed = false)
{
    public static StackUpdateOutcome Failed(string message) => new(false, message, false);

    public static StackUpdateOutcome NotStartedWith(string message) => new(false, message, false, NotStarted: true);
}

/// <summary>How a planned update comes about.</summary>
public enum PlannedKind
{
    /// <summary>The registry moved the tag (a new digest); a pull brings it.</summary>
    Digest,

    /// <summary>A newer version tag; MatDock runs it by an image override.</summary>
    Version,
}

/// <summary>One service with a newer image: what runs now and what the rule makes of it. <paramref name="Target"/> is the new image (a version) or the short registry digest.</summary>
public sealed record PlannedUpdate(string Service, string Current, string Target, PlannedKind Kind);

/// <summary>What the rule of a stack would update now.</summary>
public sealed record StackUpdatePlan(IReadOnlyList<PlannedUpdate> Items, string? Error);

/// <summary>Per-stack update check result.</summary>
public sealed record StackUpdateReport(IReadOnlyList<ImageUpdateStatus> Images, string? Error)
{
    public int UpdatesAvailable => Images.Count(i => i.UpdateAvailable);

    public static StackUpdateReport Failed(string error) => new(Array.Empty<ImageUpdateStatus>(), error);
}

/// <summary>One service whose image changed in an update run (image ids, shortened).</summary>
public sealed record UpdateChange(string Service, string Image, string Before, string After);
