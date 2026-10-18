using MatDock.Core.Execution;

namespace MatDock.Tests;

/// <summary>
/// The commands MatDock runs on its own host see only what a shell and the Docker CLI need. `docker compose` prefers a variable of its process over the
/// `.env` of a stack, so MatDock's own settings (MAT_API_KEY, MATIDENTITY_*, its connection string) would otherwise end up in every stack it deploys.
/// </summary>
public class LocalHostEnvironmentTests
{
    [Fact]
    public void MatDocks_own_settings_are_not_passed_to_the_commands()
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/app",
            ["LC_ALL"] = "C",
            ["DOCKER_HOST"] = "unix:///var/run/docker.sock",
            ["MAT_API_KEY"] = "secret",
            ["MATIDENTITY_CLIENT_ID"] = "matdock",
            ["MATIDENTITY_CLIENT_SECRET"] = "secret",
            ["MatDock__Api__Key"] = "secret",
            ["ConnectionStrings__Default"] = "Data Source=/data/matdock.db",
            ["ADMIN_PASSWORD"] = "secret",
        };

        LocalHostCommand.KeepOnlySafeVariables(env);

        Assert.Equal(["DOCKER_HOST", "HOME", "LC_ALL", "PATH"], env.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_command_on_the_host_does_not_see_them_either()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // the local session runs /bin/sh, which the container has; on a developer's Windows machine there is nothing to run
        }

        Environment.SetEnvironmentVariable("MAT_API_KEY", "leaked");
        try
        {
            using var command = new LocalHostCommand("printf '%s' \"${MAT_API_KEY:-none}|${PATH:+path}\"");
            var result = command.Execute();
            Assert.Equal("none|path", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAT_API_KEY", null);
        }
    }
}
