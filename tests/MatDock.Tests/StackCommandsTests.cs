using MatDock.Core.Stacks;
using Xunit;

namespace MatDock.Tests;

public class StackCommandsTests
{
    [Theory]
    [InlineData("media-stack")]
    [InlineData("app_01")]
    [InlineData("nginx")]
    [InlineData("a1")]
    public void Accepts_valid_project_names(string name)
        => Assert.True(StackCommands.IsValidName(name));

    [Theory]
    [InlineData("")]
    [InlineData("-leading-dash")]
    [InlineData("Upper")]           // compose project names are lowercase
    [InlineData("has space")]
    [InlineData("stack.dot")]       // dot not allowed by our name policy
    [InlineData("s;rm -rf /")]
    [InlineData("$(whoami)")]
    [InlineData("a`id`")]
    [InlineData("a|b")]
    [InlineData("a/b")]
    [InlineData("with\nnewline")]
    public void Rejects_invalid_or_injection_names(string name)
        => Assert.False(StackCommands.IsValidName(name));

    [Fact]
    public void Deploy_builds_expected_command()
    {
        var cmd = StackCommands.Deploy("docker", "media");
        Assert.Contains("name=media;", cmd);
        Assert.Contains("mkdir -p", cmd);
        Assert.Contains("cat > \"$dir/docker-compose.yml\"", cmd);  // YAML arrives on stdin, never interpolated
        Assert.Contains("docker compose -p \"$name\" up -d", cmd);
        Assert.EndsWith("2>&1", cmd);                               // output merged for the UI
    }

    [Fact]
    public void Down_builds_expected_command()
    {
        var cmd = StackCommands.Down("sudo -n docker", "media");
        Assert.Contains("name=media;", cmd);
        Assert.Contains("sudo -n docker compose -p \"$name\" down", cmd);
    }

    [Fact]
    public void Builders_throw_on_injection_name()
    {
        Assert.Throws<ArgumentException>(() => StackCommands.Deploy("docker", "evil; rm -rf /"));
        Assert.Throws<ArgumentException>(() => StackCommands.Down("docker", "$(evil)"));
    }

    [Fact]
    public void Deploy_body_is_single_quoted_and_contains_no_single_quotes()
    {
        // The whole sh -c body is wrapped in single quotes; if it contained a single quote the
        // remote shell would break out of the quoting -> the builder must never emit one.
        var cmd = StackCommands.Deploy("docker", "media");
        var start = cmd.IndexOf("sh -c '", System.StringComparison.Ordinal) + "sh -c '".Length;
        var end = cmd.LastIndexOf("' 2>&1", System.StringComparison.Ordinal);
        var body = cmd.Substring(start, end - start);
        Assert.DoesNotContain("'", body);
    }
}
