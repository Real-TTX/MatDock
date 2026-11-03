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
        StacksPath = Path.Combine(dataPath, "stacks");
        DatabasePath = Path.Combine(dataPath, "matdock.db");
    }

    public string DataPath { get; }
    public string ConfigPath { get; }
    public string KeysPath { get; }
    public string LogsPath { get; }
    public string BackupsPath { get; }

    /// <summary>Compose and <c>.env</c> files of the stacks deployed to a <em>local</em> environment
    /// (<c>&lt;data&gt;/stacks/&lt;name&gt;</c>), so they survive a re-creation of the MatDock container.</summary>
    public string StacksPath { get; }
    public string DatabasePath { get; }

    public string SqliteConnectionString => $"Data Source={DatabasePath}";

    /// <summary>
    /// A short id of this installation (8 hex characters, made once and kept in the configuration folder). Its cookies carry it in their names: a browser
    /// keeps cookies per host, not per port, so two MatDocks reached on the same host (two ports of one server, server1 and server2 by the same
    /// address) would otherwise overwrite each other's sign-in.
    /// </summary>
    public string InstanceId()
    {
        var file = Path.Combine(ConfigPath, "instance-id");
        try
        {
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: 8 } kept && kept.All(Uri.IsHexDigit))
            {
                return kept.ToLowerInvariant();
            }

            var made = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
            Directory.CreateDirectory(ConfigPath);
            File.WriteAllText(file, made);
            return made;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "00000000";
        }
    }

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
