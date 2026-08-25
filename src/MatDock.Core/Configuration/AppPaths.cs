using Microsoft.Extensions.Options;

namespace MatDock.Core.Configuration;

/// <summary>
/// Resolves the concrete file-system layout under <see cref="MatDockOptions.DataPath"/> and makes
/// sure the required directories exist. Registered as a singleton.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(IOptions<MatDockOptions> options)
        : this(options.Value.DataPath)
    {
    }

    public AppPaths(string dataPath)
    {
        DataPath = dataPath;
        ConfigPath = Path.Combine(dataPath, "config");
        KeysPath = Path.Combine(dataPath, "dataprotection-keys");
        LogsPath = Path.Combine(dataPath, "logs");
        BackupsPath = Path.Combine(dataPath, "backups");
        DatabasePath = Path.Combine(dataPath, "matdock.db");
    }

    public string DataPath { get; }
    public string ConfigPath { get; }
    public string KeysPath { get; }
    public string LogsPath { get; }
    public string BackupsPath { get; }
    public string DatabasePath { get; }

    public string SqliteConnectionString => $"Data Source={DatabasePath}";

    /// <summary>Creates the data directories if they do not yet exist.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(ConfigPath);
        Directory.CreateDirectory(KeysPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(BackupsPath);
    }
}
