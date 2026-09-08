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

/// <summary>How MatDock reaches the Docker daemon for an environment.</summary>
public enum ConnectionType
{
    /// <summary>Run the Docker CLI on a remote host over SSH (default).</summary>
    Ssh = 0,

    /// <summary>Run the Docker CLI locally on the MatDock host, talking to the mounted Docker socket.</summary>
    Local = 1
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

/// <summary>How MatDock authenticates against a Git remote when cloning stack repositories.</summary>
public enum GitAuthType
{
    /// <summary>HTTPS with username + personal access token (default).</summary>
    HttpsToken = 0

    // Future: SshKey = 1
}

/// <summary>When a sync job actually redeploys its stacks on a schedule/webhook trigger.</summary>
public enum SyncUpdateMode
{
    /// <summary>Only redeploy when the repo's commit changed since the last run (default).</summary>
    OnGitChange = 0,

    /// <summary>Redeploy on every trigger (idempotent compose up; pairs well with "pull images" for :latest).</summary>
    Always = 1
}

/// <summary>Which reverse-proxy MatDock manages routes on.</summary>
public enum ProxyProviderType
{
    /// <summary>Caddy server via its JSON Admin API (default port 2019).</summary>
    Caddy = 0,

    /// <summary>Matcad reverse proxy via its REST API.</summary>
    Matcad = 1
}

/// <summary>What fires a scheduled task: a cron time trigger or an internal event.</summary>
public enum ScheduleTrigger
{
    Cron = 0,
    Event = 1
}

/// <summary>What a scheduled task does when it fires.</summary>
public enum ScheduleAction
{
    PruneVolumes = 0,
    PruneImages = 1,
    PruneNetworks = 2,
    Summary = 3,
    Backup = 4,
    HealthAlert = 5,
    Sync = 6
}

/// <summary>Internal events that can trigger a scheduled task (Phase 2).</summary>
public enum ScheduleEvent
{
    DeployFailed = 0,
    BackupFailed = 1,
    SyncFailed = 2,
    EnvironmentOffline = 3,
    ContainerDied = 4
}

/// <summary>Where backup archives are stored.</summary>
public enum BackupTargetType
{
    /// <summary>MatDock's own data volume (/data/backups).</summary>
    Local = 0,

    /// <summary>A network share via SMB/CIFS (e.g. a NAS).</summary>
    Smb = 1
}
