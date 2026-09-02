namespace MatDock.Core.Entities;

/// <summary>
/// A reusable, environment-independent compose blueprint ("App"). Installing a template creates a new
/// <see cref="Stack"/> pre-filled with the template's YAML; the target environment and stack name are
/// chosen at install time. Mapped to the <c>StackTemplate</c> table.
/// </summary>
public class StackTemplate : AuditableEntity
{
    /// <summary>Free-form display name (e.g. "Nginx Web Server").</summary>
    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string? Description { get; set; }

    /// <summary>Optional URL to an app logo shown on the Apps page. Empty falls back to a letter avatar.</summary>
    public string? IconUrl { get; set; }

    public string ComposeYaml { get; set; } = string.Empty;
}
