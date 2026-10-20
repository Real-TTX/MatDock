using MatDock.Core.Docker;
using MatDock.Core.Entities;

namespace MatDock.Web.Support;

/// <summary>How an environment is shown in lists.</summary>
public static class EnvironmentView
{
    /// <summary>Where MatDock reaches it: <c>user@host:port</c> over SSH, "Docker socket" for a Local environment.</summary>
    public static string Address(DockerEnvironment env)
        => env.ConnectionType switch
        {
            ConnectionType.Local => "Docker socket",
            ConnectionType.Tcp => $"tcp://{env.Host}:{env.Port}{(env.TlsCert is null ? " (no TLS)" : string.Empty)}",
            _ => $"{env.Username}@{env.Host}:{env.Port}",
        };

    /// <summary>The tone of a usage meter (MatUi <c>data-tone</c>): ok below 70 %, warn below 90 %, error above.</summary>
    public static string Tone(double percent) => percent >= 90 ? "error" : percent >= 70 ? "warn" : "ok";

    /// <summary>A percentage for a meter's <c>data-value</c>: 0 to 100, whole numbers.</summary>
    public static int Percent(double? percent) => (int)Math.Round(Math.Clamp(percent ?? 0, 0, 100));
}

/// <summary>The tile of an environment (dashboard and the gallery of the environments): its usage, its links.</summary>
/// <param name="OnDashboard">The dashboard shows the CPU in percent and the history of the last 24 h; the gallery the load, the Docker version and when it was checked.</param>
public sealed record EnvCardModel(DockerEnvironment Env, HostStats Stats, int? Running, bool OnDashboard);
