using MatDock.Core.Entities;

namespace MatDock.Web.Support;

/// <summary>How an environment is shown in lists.</summary>
public static class EnvironmentView
{
    /// <summary>Where MatDock reaches it: <c>user@host:port</c> over SSH, "Docker socket" for a Local environment.</summary>
    public static string Address(DockerEnvironment env)
        => env.ConnectionType == ConnectionType.Local ? "Docker socket" : $"{env.Username}@{env.Host}:{env.Port}";
}
