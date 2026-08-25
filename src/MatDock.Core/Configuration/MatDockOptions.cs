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

    public AdminSeedOptions Admin { get; set; } = new();
}

/// <summary>Credentials used to seed the first administrator on an empty database.</summary>
public sealed class AdminSeedOptions
{
    public string Username { get; set; } = "admin";

    public string DisplayName { get; set; } = "Administrator";

    /// <summary>Initial password. The seeded admin is forced to change it on first login.</summary>
    public string Password { get; set; } = "admin";
}
