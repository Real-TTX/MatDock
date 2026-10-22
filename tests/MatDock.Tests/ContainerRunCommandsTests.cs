using MatDock.Core.Containers;
using Xunit;

namespace MatDock.Tests;

public class ContainerRunCommandsTests
{
    private static ContainerRunSpec Valid() => new() { Image = "nginx:alpine" };

    [Fact]
    public void Run_builds_a_minimal_command()
    {
        var cmd = ContainerRunCommands.Run(Valid(), "docker");
        Assert.Contains("docker run -d 'nginx:alpine'", cmd);
        Assert.DoesNotContain("--restart", cmd); // "no" is Docker's default
        Assert.EndsWith("2>&1", cmd);
    }

    [Fact]
    public void Run_adds_every_option_quoted()
    {
        var spec = new ContainerRunSpec
        {
            Image = "ghcr.io/org/app:1.2",
            Name = "web",
            Restart = "unless-stopped",
            Network = "app-net",
            Ports = { "8080:80", "127.0.0.1:9000:9000", "53:53/udp" },
            Env = { "TZ=Europe/Berlin", "GREETING=hello world" },
            Volumes = { "app-data:/data", "/srv/files:/files:ro" },
        };

        var cmd = ContainerRunCommands.Run(spec, "sudo -n docker");
        Assert.Contains("sudo -n docker run -d", cmd);
        Assert.Contains("--name 'web'", cmd);
        Assert.Contains("--restart 'unless-stopped'", cmd);
        Assert.Contains("--network 'app-net'", cmd);
        Assert.Contains("-p '8080:80'", cmd);
        Assert.Contains("-p '127.0.0.1:9000:9000'", cmd);
        Assert.Contains("-p '53:53/udp'", cmd);
        Assert.Contains("-e 'GREETING=hello world'", cmd); // a value with a space stays one argument
        Assert.Contains("-v 'app-data:/data'", cmd);
        Assert.Contains("-v '/srv/files:/files:ro'", cmd);
        Assert.Contains("'ghcr.io/org/app:1.2' 2>&1", cmd); // the image comes last
    }

    [Fact]
    public void An_env_value_with_a_quote_cannot_break_out()
    {
        var spec = Valid();
        spec.Env.Add("X=a'; touch /tmp/pwned; echo '");
        var cmd = ContainerRunCommands.Run(spec);
        Assert.Contains("-e 'X=a'\\''; touch /tmp/pwned; echo '\\'''", cmd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("evil;rm -rf /")]
    [InlineData("$(whoami)")]
    [InlineData("-leading")]
    public void A_bad_image_is_refused(string image)
        => Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = image }));

    [Theory]
    [InlineData("80")]
    [InlineData("8080:80;id")]
    [InlineData("0:80")]
    [InlineData("70000:80")]
    [InlineData("8080:80/icmp")]
    [InlineData("a:b")]
    public void A_bad_port_is_refused(string port)
    {
        var spec = Valid();
        spec.Ports.Add(port);
        Assert.NotNull(ContainerRunCommands.Validate(spec));
    }

    [Theory]
    [InlineData("noequals")]
    [InlineData("1BAD=x")]
    [InlineData("A B=x")]
    [InlineData("=x")]
    public void A_bad_env_entry_is_refused(string entry)
    {
        var spec = Valid();
        spec.Env.Add(entry);
        Assert.NotNull(ContainerRunCommands.Validate(spec));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("data:data")]
    [InlineData("data:/d:rw")]
    [InlineData("rel/path:/d")]
    [InlineData("/h:rel")]
    [InlineData("/h:/d e")]
    [InlineData("/h:/d'x")]
    [InlineData("/h:/d\\x")]
    public void A_bad_volume_is_refused(string volume)
    {
        var spec = Valid();
        spec.Volumes.Add(volume);
        Assert.NotNull(ContainerRunCommands.Validate(spec));
    }

    [Fact]
    public void A_bad_name_restart_or_network_is_refused()
    {
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "a", Name = "a b" }));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "a", Restart = "sometimes" }));
        Assert.NotNull(ContainerRunCommands.Validate(new ContainerRunSpec { Image = "a", Network = "n;id" }));
        Assert.Throws<ArgumentException>(() => ContainerRunCommands.Run(new ContainerRunSpec { Image = "x y" }));
    }
}
