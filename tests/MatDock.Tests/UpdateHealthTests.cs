using MatDock.Core.Containers;
using MatDock.Core.Images;
using MatDock.Core.Updates;
using MatDock.Core.Volumes;
using Xunit;

namespace MatDock.Tests;

public class UpdateHealthTests
{
    private static DockerContainer C(string state, string status, string name = "web") => new() { Id = name, Name = name, State = state, Status = status };

    [Fact]
    public void Running_without_a_failing_or_starting_check_is_healthy()
    {
        Assert.Equal(StackHealth.Healthy, UpdateHealth.Evaluate([C("running", "Up 5 seconds"), C("running", "Up 4 seconds (healthy)", "db")]).Health);
        Assert.Equal(StackHealth.Starting, UpdateHealth.Evaluate([C("running", "Up 2 seconds (health: starting)")]).Health);
        Assert.Equal(StackHealth.Starting, UpdateHealth.Evaluate([C("restarting", "Restarting (1) 1 second ago")]).Health);
    }

    [Fact]
    public void An_unhealthy_or_stopped_container_fails_the_gate()
    {
        var (health, reason) = UpdateHealth.Evaluate([C("running", "Up 1 minute (healthy)", "db"), C("running", "Up 1 minute (unhealthy)")]);
        Assert.Equal(StackHealth.Failed, health);
        Assert.Equal("web is unhealthy.", reason);
        Assert.Equal(StackHealth.Failed, UpdateHealth.Evaluate([C("exited", "Exited (1) 3 seconds ago")]).Health);
        Assert.Equal(StackHealth.Failed, UpdateHealth.Evaluate([]).Health);
    }

    [Fact]
    public void The_images_before_the_update_are_read_and_can_be_tagged_back()
    {
        var cmd = ContainerCommands.ImagesOf("docker", ["abc123", "def456"]);
        Assert.Equal(VolumeCommands.PathPrefix + "docker inspect --format '{{index .Config.Labels \"com.docker.compose.service\"}} {{.Config.Image}} {{.Image}}' 'abc123' 'def456'", cmd);
        Assert.Throws<ArgumentException>(() => ContainerCommands.ImagesOf("docker", ["x;reboot"]));

        var parsed = ContainerCommands.ParseImagesOf("web nginx:latest sha256:aaa\ndb postgres:16 sha256:bbb\ngarbage\n");
        Assert.Equal([("web", "nginx:latest", "sha256:aaa"), ("db", "postgres:16", "sha256:bbb")], parsed);

        Assert.Equal(VolumeCommands.PathPrefix + "docker image tag 'sha256:aaa' 'nginx:latest'", ImageCommands.Tag("sha256:aaa", "nginx:latest"));
    }
}

public class RollbackReferenceTests
{
    [Theory]
    [InlineData("nginx:1.27", "nginx:matdock-rollback-1.27")]
    [InlineData("nginx", "nginx:matdock-rollback-latest")]
    [InlineData("localhost:5099/team/app:latest", "localhost:5099/team/app:matdock-rollback-latest")]
    [InlineData("registry.example.com:5000/app", "registry.example.com:5000/app:matdock-rollback-latest")]
    public void The_old_image_keeps_a_name_of_its_own(string reference, string expected)
        => Assert.Equal(expected, ImageCommands.RollbackReference(reference));

    [Fact]
    public void A_digest_pin_needs_none()
        => Assert.Null(ImageCommands.RollbackReference("nginx@sha256:abc"));
}
