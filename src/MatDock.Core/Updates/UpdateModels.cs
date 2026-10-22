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
public sealed record StackUpdateOutcome(bool Ok, string Message, bool Changed)
{
    public static StackUpdateOutcome Failed(string message) => new(false, message, false);
}

/// <summary>Per-stack update check result.</summary>
public sealed record StackUpdateReport(IReadOnlyList<ImageUpdateStatus> Images, string? Error)
{
    public int UpdatesAvailable => Images.Count(i => i.UpdateAvailable);

    public static StackUpdateReport Failed(string error) => new(Array.Empty<ImageUpdateStatus>(), error);
}

/// <summary>One service whose image changed in an update run (image ids, shortened).</summary>
public sealed record UpdateChange(string Service, string Image, string Before, string After);
