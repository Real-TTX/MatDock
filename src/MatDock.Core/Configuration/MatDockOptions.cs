namespace MatDock.Core.Configuration;

/// <summary>
/// Strongly-typed application options bound from configuration (section <c>MatDock</c>) and
/// environment variables. Everything the app writes lives under <see cref="DataPath"/>, which is
/// expected to be a mounted Docker volume so data survives container restarts.
/// </summary>
public sealed class MatDockOptions
{
    public const string SectionName = "MatDock";

    /// <summary>Root data directory (database, config, keys, logs). Container default: <c>/data</c>.</summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>How long a login session stays valid, in days.</summary>
    public int SessionLifetimeDays { get; set; } = 30;

    /// <summary>Default timeout (seconds) for SSH connect / command operations.</summary>
    public int SshTimeoutSeconds { get; set; } = 20;

    /// <summary>Timeout (seconds) for a full volume migration; migrations can take a while.</summary>
    public int MigrationTimeoutSeconds { get; set; } = 3600;

    /// <summary>Tiny helper image used on the remote hosts to tar/untar volume contents.</summary>
    public string HelperImage { get; set; } = "busybox";

    /// <summary>The <c>restic</c> program for restic backup targets (in the image: <c>/usr/local/bin/restic</c>, found through the PATH).</summary>
    public string ResticPath { get; set; } = "restic";

    /// <summary>The image that scans images for vulnerabilities (Trivy), run on the Docker host of the image.</summary>
    public string ScannerImage { get; set; } = "aquasec/trivy:latest";

    public AdminSeedOptions Admin { get; set; } = new();

    public ApiOptions Api { get; set; } = new();

    public EnvironmentsOptions Environments { get; set; } = new();
}

/// <summary>Credentials used to seed the first administrator on an empty database (headless setups).</summary>
public sealed class AdminSeedOptions
{
    /// <summary>When true, seed the admin from these settings on an empty DB (headless/automated
    /// deployments). When false (default), an empty DB shows the first-run setup wizard instead.</summary>
    public bool Seed { get; set; }

    public string Username { get; set; } = "admin";

    public string DisplayName { get; set; } = "Administrator";

    /// <summary>Initial password. The seeded admin is forced to change it on first login.</summary>
    public string Password { get; set; } = "admin";
}

/// <summary>The machine-to-machine REST API under <c>/api/v1</c> (used by Mattiverse).</summary>
public sealed class ApiOptions
{
    /// <summary>Shared secret expected in the <c>X-Api-Key</c> header (env <c>MatDock__Api__Key</c>). Empty = the API
    /// is disabled and answers 503, so it can never be reached without a key.</summary>
    public string? Key { get; set; }
}

/// <summary>Start-up behaviour for environments.</summary>
public sealed class EnvironmentsOptions
{
    /// <summary>When true (env <c>MatDock__Environments__EnsureLocal=true</c>), a <em>Local</em> environment named
    /// <c>local</c> is created at start if none exists and the Docker socket is reachable.</summary>
    public bool EnsureLocal { get; set; }
}
