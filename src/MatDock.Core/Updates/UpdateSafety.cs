namespace MatDock.Core.Updates;

/// <summary>The health gate of an update: how long the containers get to be up and healthy, and whether a failure is rolled back.</summary>
public sealed record UpdateSafety(TimeSpan Grace, bool Rollback)
{
    public static readonly UpdateSafety Default = new(TimeSpan.FromSeconds(90), Rollback: true);
}
