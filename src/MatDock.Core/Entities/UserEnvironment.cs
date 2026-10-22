namespace MatDock.Core.Entities;

/// <summary>
/// One environment a restricted user may see and control (see <see cref="User.RestrictEnvironments"/>). Without any row such a user sees none.
/// Administrators and users that are not restricted ignore these rows.
/// </summary>
public class UserEnvironment : AuditableEntity
{
    public long UserId { get; set; }

    public long EnvironmentId { get; set; }
}
