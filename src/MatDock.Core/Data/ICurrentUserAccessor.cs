namespace MatDock.Core.Data;

/// <summary>
/// Supplies the id of the user on whose behalf the current unit of work runs.
/// Implemented in the web layer from the authenticated principal; returns <c>null</c>
/// for system operations (startup seeding, migrations, background jobs).
/// </summary>
public interface ICurrentUserAccessor
{
    long? UserId { get; }
}

/// <summary>Fallback accessor for system contexts (no authenticated user).</summary>
public sealed class SystemCurrentUserAccessor : ICurrentUserAccessor
{
    public long? UserId => null;
}
