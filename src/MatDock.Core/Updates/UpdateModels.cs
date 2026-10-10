namespace MatDock.Core.Updates;

/// <summary>Update status of one image used by a stack: local vs. registry digest.</summary>
public sealed record ImageUpdateStatus(
    string Service,
    string Image,
    string? LocalDigest,
    string? RegistryDigest,
    bool UpdateAvailable,
    string? Note);

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
