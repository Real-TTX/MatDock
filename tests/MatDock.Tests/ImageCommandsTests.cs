using MatDock.Core.Images;
using Xunit;

namespace MatDock.Tests;

public class ImageCommandsTests
{
    [Theory]
    [InlineData("nginx:latest")]
    [InlineData("ghcr.io/org/app:1.2.3")]
    [InlineData("registry.example.com:5000/team/app:tag")]
    [InlineData("sha256:abc123")]
    [InlineData("a1b2c3d4e5f6")]
    [InlineData("repo@sha256:deadbeef")]
    public void Accepts_valid_image_refs(string reference)
        => Assert.True(ImageCommands.IsValidImageRef(reference));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("evil;rm -rf /")]
    [InlineData("$(whoami)")]
    [InlineData("a`id`")]
    [InlineData("a|b")]
    [InlineData("-leading")]
    [InlineData("with\nnewline")]
    public void Rejects_invalid_or_injection_refs(string reference)
        => Assert.False(ImageCommands.IsValidImageRef(reference));

    [Fact]
    public void Pull_builds_expected_and_validates()
    {
        var cmd = ImageCommands.Pull("nginx:alpine");
        Assert.Contains("docker pull 'nginx:alpine'", cmd);
        Assert.EndsWith("2>&1", cmd);
        Assert.Throws<System.ArgumentException>(() => ImageCommands.Pull("$(evil)"));
    }

    [Fact]
    public void A_pull_with_progress_goes_through_compose_where_it_has_json_progress()
    {
        var cmd = ImageCommands.PullWithProgress("nginx:alpine");
        Assert.Contains("docker compose --progress json version", cmd);
        Assert.Contains(@"printf 'services:\n  image:\n    image: %s\n' 'nginx:alpine' | docker compose --progress json -p matdock-pull -f - pull 2>&1", cmd);
        Assert.Contains("else docker pull 'nginx:alpine' 2>&1; fi", cmd);
        Assert.Throws<System.ArgumentException>(() => ImageCommands.PullWithProgress("$(evil)"));
    }

    /// <summary>The command in a real shell (Linux) against a stand-in docker: compose gets the image as its one service, an old docker the plain pull.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_pull_with_progress_runs_in_a_real_shell(bool composeHasJson)
    {
        if (!File.Exists("/bin/sh"))
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("matdock-pull").FullName;
        try
        {
            var docker = Path.Combine(dir, "docker");
            File.WriteAllText(docker, "#!/bin/sh\n"
                + "if [ \"$1 $2 $3 $4\" = \"compose --progress json version\" ]; then exit " + (composeHasJson ? "0" : "1") + "; fi\n"
                + "if [ \"$1\" = compose ]; then cat > \"" + dir + "/compose.yml\"; echo \"compose $*\"; exit 0; fi\n"
                + "echo \"plain $*\"\n");
            File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(ImageCommands.PullWithProgress("registry.example.com/team/app:1.2", docker));
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);

            Assert.Equal(0, p.ExitCode);
            if (composeHasJson)
            {
                Assert.Equal("compose compose --progress json -p matdock-pull -f - pull", output.Trim());
                Assert.Equal("services:\n  image:\n    image: registry.example.com/team/app:1.2\n", File.ReadAllText(Path.Combine(dir, "compose.yml")));
            }
            else
            {
                Assert.Equal("plain pull registry.example.com/team/app:1.2", output.Trim());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void List_builds_json_format_command()
    {
        var cmd = ImageCommands.List("docker");
        Assert.Contains("docker image ls --digests --format '{{json .}}'", cmd);
    }

    [Fact]
    public void Remove_quotes_reference_and_omits_force_by_default()
    {
        var cmd = ImageCommands.Remove("nginx:latest", force: false, "sudo -n docker");
        Assert.Contains("sudo -n docker image rm 'nginx:latest'", cmd);
        Assert.DoesNotContain(" -f ", cmd);
    }

    [Fact]
    public void Remove_adds_force_flag_when_requested()
    {
        var cmd = ImageCommands.Remove("nginx:latest", force: true, "docker");
        Assert.Contains("docker image rm -f 'nginx:latest'", cmd);
    }

    [Fact]
    public void Remove_throws_on_injection_reference()
        => Assert.Throws<System.ArgumentException>(() => ImageCommands.Remove("evil; rm -rf /", force: false, "docker"));

    [Fact]
    public void Prune_dangling_by_default_and_all_when_requested()
    {
        Assert.Contains("docker image prune -f", ImageCommands.Prune(all: false, "docker"));
        Assert.DoesNotContain("-a", ImageCommands.Prune(all: false, "docker"));
        Assert.Contains("docker image prune -f -a", ImageCommands.Prune(all: true, "docker"));
    }
}
