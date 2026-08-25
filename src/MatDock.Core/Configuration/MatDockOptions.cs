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

    public AdminSeedOptions Admin { get; set; } = new();

    /// <summary>
    /// Dev-only: seed a ready-to-use test account (no forced password change) so testing never has to
    /// touch the real admin. Enabled in <c>docker-compose.dev.yml</c>; off by default (never in release).
    /// </summary>
    public bool SeedTestUser { get; set; }

    public TestUserSeedOptions TestUser { get; set; } = new();
}

/// <summary>Credentials for the optional dev test account.</summary>
public sealed class TestUserSeedOptions
{
    public string Username { get; set; } = "tester";

    public string DisplayName { get; set; } = "Test-Benutzer";

    public string Password { get; set; } = "Tester123!";
}

/// <summary>Credentials used to seed the first administrator on an empty database.</summary>
public sealed class AdminSeedOptions
{
    public string Username { get; set; } = "admin";

    public string DisplayName { get; set; } = "Administrator";

    /// <summary>Initial password. The seeded admin is forced to change it on first login.</summary>
    public string Password { get; set; } = "admin";
}
