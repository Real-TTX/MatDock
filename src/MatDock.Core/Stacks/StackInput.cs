namespace MatDock.Core.Stacks;

/// <summary>Create/update carrier for a managed stack.</summary>
public sealed class StackInput
{
    public string Name { get; set; } = string.Empty;
    public long EnvironmentId { get; set; }
    public string ComposeYaml { get; set; } = string.Empty;

    /// <summary>Optional <c>.env</c> file contents (inline stacks); for a Git stack the variables that override the repository's <c>.env</c>.</summary>
    public string? EnvContent { get; set; }

    // Optional Git source. When GitRepoUrl is set the stack is git-backed and ComposeYaml is a cache.
    public string? GitRepoUrl { get; set; }
    public string? GitReference { get; set; }
    public string? GitComposePath { get; set; }
    public long? GitCredentialId { get; set; }

    /// <summary>An env file of the repository besides its <c>.env</c>, relative to the compose file's folder (Git stacks).</summary>
    public string? GitEnvFile { get; set; }

    /// <summary>The cron of the automatic sync (Git stacks); null or empty = none.</summary>
    public string? GitSyncCron { get; set; }

    /// <summary>The tags (comma-separated names); null keeps the stored ones, empty removes them.</summary>
    public string? Tags { get; set; }

    /// <summary>The icon chosen in MatDock (<see cref="StackIcons"/>); null keeps the stored one, empty removes it.</summary>
    public string? Icon { get; set; }

    /// <summary>Owner of an externally managed stack (e.g. <c>mattiverse</c>). <c>null</c> keeps the stored value, so a
    /// save from MatDock's own editor never drops it; an empty string clears it.</summary>
    public string? ManagedBy { get; set; }
}

/// <summary>A compose file found in a folder of a host (<see cref="StackService.ScanFolderAsync"/>): where, the stack name it would get, whether such a
/// project runs already and whether MatDock manages a stack of that name.</summary>
public sealed record FoundCompose(string Path, string Folder, string FileName, string Name, bool Running, bool Managed);
