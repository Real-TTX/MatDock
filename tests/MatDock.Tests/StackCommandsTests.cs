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
    public void Login_reads_password_from_stdin_and_quotes_host_and_user()
    {
        var cmd = StackCommands.Login("sudo -n docker", "ghcr.io", "me");
        Assert.Contains("sudo -n docker login 'ghcr.io' -u 'me' --password-stdin", cmd);
        Assert.DoesNotContain("-p ", cmd);   // never put the secret on the command line
        Assert.EndsWith("2>&1", cmd);
    }

    [Fact]
    public void Login_single_quotes_a_malicious_user()
    {
        var cmd = StackCommands.Login("docker", "reg.example.com", "a'b");
        Assert.Contains("-u 'a'\\''b'", cmd);
    }

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
    public void Update_pulls_before_up()
    {
        var cmd = StackCommands.Update("docker", "media");
        Assert.Contains("cat > \"$dir/docker-compose.yml\"", cmd);
        Assert.Contains("docker compose -p \"$name\" pull;", cmd);
        Assert.Contains("docker compose -p \"$name\" up -d", cmd);
        Assert.EndsWith("2>&1", cmd);
    }

    [Fact]
    public void Down_builds_expected_command()
    {
        var cmd = StackCommands.Down("sudo -n docker", "media", "docker-compose.yml");
        Assert.Contains("set -e;", cmd);   // abort early if the stack dir is missing
        Assert.Contains("sudo -n docker compose -p \"$1\" -f \"$2\" down", cmd);
        Assert.Contains("sh 'media' 'docker-compose.yml'", cmd); // name + path passed as quoted args
    }

    [Fact]
    public void GitSync_builds_expected_command()
    {
        var cmd = StackCommands.GitSync("docker", "media", "stacks/app/compose.yml");
        Assert.Contains("tar -C \"$dir\" -xf -", cmd);                 // extract repo tarball from stdin
        Assert.DoesNotContain("-delete", cmd);                         // never wipe (preserve runtime data)
        Assert.Contains("docker compose -p \"$1\" -f \"$2\" up -d", cmd);
        Assert.Contains("sh 'media' 'stacks/app/compose.yml'", cmd);
    }

    [Fact]
    public void GitSync_with_pull_pulls_before_up()
    {
        var cmd = StackCommands.GitSync("docker", "media", "compose.yml", pull: true);
        Assert.Contains("docker compose -p \"$1\" -f \"$2\" pull;", cmd);
        Assert.Contains("docker compose -p \"$1\" -f \"$2\" up -d", cmd);
    }

    [Fact]
    public void GitSync_without_pull_does_not_pull()
    {
        var cmd = StackCommands.GitSync("docker", "media", "compose.yml", pull: false);
        Assert.DoesNotContain(" pull;", cmd);
    }

    [Fact]
    public void GitSync_single_quotes_a_malicious_compose_path()
    {
        // A path containing a single quote must be safely quoted, not break out of the arg.
        var cmd = StackCommands.GitSync("docker", "media", "a'b");
        Assert.Contains("'a'\\''b'", cmd);
    }

    [Fact]
    public void Builders_throw_on_injection_name()
    {
        Assert.Throws<ArgumentException>(() => StackCommands.Deploy("docker", "evil; rm -rf /"));
        Assert.Throws<ArgumentException>(() => StackCommands.Down("docker", "$(evil)", "docker-compose.yml"));
        Assert.Throws<ArgumentException>(() => StackCommands.GitSync("docker", "$(evil)", "docker-compose.yml"));
    }

    [Theory]
    [InlineData("Nginx Web Server", "nginx-web-server")]
    [InlineData("  My  App!! ", "my-app")]
    [InlineData("UPPER_case", "upper-case")]
    [InlineData("a.b.c", "a-b-c")]
    [InlineData("---leading", "leading")]
    [InlineData("café ☕ bar", "caf-bar")]
    public void Slugify_produces_valid_names(string title, string expected)
    {
        var slug = StackCommands.Slugify(title);
        Assert.Equal(expected, slug);
        Assert.True(StackCommands.IsValidName(slug));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData(null)]
    public void Slugify_falls_back_to_app_for_empty(string? title)
    {
        var slug = StackCommands.Slugify(title);
        Assert.Equal("app", slug);
        Assert.True(StackCommands.IsValidName(slug));
    }

    [Fact]
    public void Slugify_caps_length_and_stays_valid()
    {
        var slug = StackCommands.Slugify(new string('a', 200));
        Assert.True(slug.Length <= 63);
        Assert.True(StackCommands.IsValidName(slug));
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
