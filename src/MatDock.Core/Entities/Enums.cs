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
    Local = 1,

    /// <summary>Run the Docker CLI on the MatDock host against a remote Docker API (tcp://host:port, with TLS client certificates).</summary>
    Tcp = 2
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
    HttpsToken = 0,

    /// <summary>SSH with a private key (ssh:// or git@host:path URLs).</summary>
    SshKey = 1
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
    Sync = 6,

    /// <summary>Full stack backups (bundles) of one stack or of every stack of an owner, with retention.</summary>
    BundleBackup = 7,

    /// <summary>Sends the event that fired the task as a notification (event trigger only).</summary>
    Notify = 8,

    /// <summary>Checks the managed stacks for newer images; notifies, or pulls and redeploys them.</summary>
    UpdateStacks = 9,

    /// <summary>Scans the images of the running containers for known vulnerabilities (Trivy) and notifies about critical and high ones.</summary>
    ScanImages = 10
}

/// <summary>Why a stack bundle was taken.</summary>
public enum StackBackupKind
{
    Manual = 0,
    Scheduled = 1,
    PreUpdate = 2
}

/// <summary>State of a stack bundle backup.</summary>
public enum StackBackupStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2
}

/// <summary>Internal events that can trigger a scheduled task.</summary>
public enum ScheduleEvent
{
    DeployFailed = 0,
    BackupFailed = 1,
    SyncFailed = 2,
    EnvironmentOffline = 3,

    /// <summary>A container stopped on its own with an error (exit code not 0, or killed for lack of memory), not by a stop.</summary>
    ContainerDied = 4,

    /// <summary>The health check of a container turned unhealthy.</summary>
    ContainerUnhealthy = 5
}

/// <summary>Where backup archives are stored.</summary>
public enum BackupTargetType
{
    /// <summary>MatDock's own data volume (/data/backups).</summary>
    Local = 0,

    /// <summary>A network share via SMB/CIFS (e.g. a NAS).</summary>
    Smb = 1
}
