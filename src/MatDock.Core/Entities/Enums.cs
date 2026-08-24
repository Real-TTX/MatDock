namespace MatDock.Core.Entities;

/// <summary>Simple role model: Admin, User and Anonymous (link sharing, later).</summary>
public enum UserRole
{
    Anonymous = 0,
    User = 1,
    Admin = 2
}

/// <summary>How MatDock authenticates against a remote SSH host.</summary>
public enum AuthType
{
    Password = 0,
    PrivateKey = 1
}

/// <summary>Soft-delete / audit state for every persisted record.</summary>
public enum UpdateState
{
    Deleted = 0,
    Created = 1,
    Updated = 2
}

/// <summary>Last known connectivity state of a Docker environment.</summary>
public enum EnvironmentStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
    Error = 3
}
