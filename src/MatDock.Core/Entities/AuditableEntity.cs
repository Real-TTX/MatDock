namespace MatDock.Core.Entities;

/// <summary>
/// Base type for every persisted record. Enforces the project database conventions:
/// a <c>BIGINT</c> primary key named <see cref="Id"/> plus create/update audit columns
/// and a soft-delete marker (<see cref="UpdateState"/>).
/// </summary>
public abstract class AuditableEntity
{
    /// <summary>Primary key (BIGINT). Assigned by the database.</summary>
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    /// <summary>Id of the user that created the record; <c>null</c> for system operations.</summary>
    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    /// <summary>Id of the user that last changed the record; <c>null</c> for system operations.</summary>
    public long? UpdateUserId { get; set; }

    /// <summary>0 = Deleted, 1 = Created, 2 = Updated.</summary>
    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}
