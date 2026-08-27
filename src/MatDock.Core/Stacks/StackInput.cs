namespace MatDock.Core.Stacks;

/// <summary>Create/update carrier for a managed stack.</summary>
public sealed class StackInput
{
    public string Name { get; set; } = string.Empty;
    public long EnvironmentId { get; set; }
    public string ComposeYaml { get; set; } = string.Empty;
}
