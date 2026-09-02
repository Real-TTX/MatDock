namespace MatDock.Core.Templates;

/// <summary>Create/update carrier for an app template (compose blueprint).</summary>
public sealed class StackTemplateInput
{
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? IconUrl { get; set; }
    public string ComposeYaml { get; set; } = string.Empty;
}
