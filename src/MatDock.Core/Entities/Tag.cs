namespace MatDock.Core.Entities;

/// <summary>
/// A tag of the catalog (one list for all environments): its name and colour. Stacks carry tag names (<see cref="Stack.Tags"/>), containers can name tags in
/// their labels (<c>matdock.tags</c>, <c>dockhand.tags</c>); the lists filter by them. A name a stack uses that is not in the catalog shows without a colour.
/// </summary>
public class Tag : AuditableEntity
{
    /// <summary>Up to 32 characters, unique (letters case-insensitively).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The colour as <c>#rrggbb</c>, or null for none.</summary>
    public string? Color { get; set; }
}
