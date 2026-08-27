namespace MatDock.Core.Stacks;

/// <summary>Create/update carrier for a managed stack.</summary>
public sealed class StackInput
{
    public string Name { get; set; } = string.Empty;
    public long EnvironmentId { get; set; }
    public string ComposeYaml { get; set; } = string.Empty;

    // Optional Git source. When GitRepoUrl is set the stack is git-backed and ComposeYaml is a cache.
    public string? GitRepoUrl { get; set; }
    public string? GitReference { get; set; }
    public string? GitComposePath { get; set; }
    public long? GitCredentialId { get; set; }
}
