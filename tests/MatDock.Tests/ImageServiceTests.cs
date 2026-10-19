using MatDock.Core.Images;
using MatDock.Core.Volumes;
using Xunit;

namespace MatDock.Tests;

public class ImageServiceTests
{
    private const string Inspect = """
        {"Id":"sha256:294b","RepoTags":["alpine:latest"],"RepoDigests":["alpine@sha256:294b"],"Created":"2026-09-17T20:37:20.889221879Z","Config":{"Env":["SECRET=hunter2"],"Cmd":["/bin/sh","-c","echo hi there"],"Entrypoint":["/entry.sh"],"WorkingDir":"/app","User":"1000","ExposedPorts":{"8080/tcp":{},"443/tcp":{}},"Labels":{"org.opencontainers.image.version":"3.24"}},"Architecture":"arm64","Variant":"v8","Os":"linux","Size":12932203}
        """;

    private const string History = """
        {"Comment":"buildkit.dockerfile.v0","CreatedAt":"2026-09-17T22:37:20+02:00","CreatedBy":"CMD [\"/bin/sh\"]","CreatedSince":"4 weeks ago","ID":"sha256:294b","Size":"0B"}
        {"Comment":"","CreatedAt":"2026-09-17T22:37:20+02:00","CreatedBy":"/bin/sh -c #(nop) ADD file:abc in / ","CreatedSince":"4 weeks ago","ID":"<missing>","Size":"9.08MB"}
        not json
        """;

    [Fact]
    public void Reads_the_image_without_its_environment()
    {
        var layers = ImageService.ParseHistory(History);
        var d = ImageService.ParseInspect(Inspect, layers)!;

        Assert.Equal("sha256:294b", d.Id);
        Assert.Equal(["alpine:latest"], d.Tags);
        Assert.Equal(12932203, d.SizeBytes);
        Assert.Equal("linux/arm64/v8", d.Platform);
        Assert.Equal("/entry.sh", d.Entrypoint);
        Assert.Equal("/bin/sh -c \"echo hi there\"", d.Command);
        Assert.Equal("/app", d.WorkingDir);
        Assert.Equal(["443/tcp", "8080/tcp"], d.ExposedPorts);
        Assert.Equal("3.24", d.Labels["org.opencontainers.image.version"]);
        Assert.Equal(new DateTime(2026, 9, 17, 20, 37, 20, DateTimeKind.Utc), d.CreatedUtc!.Value.AddTicks(-(d.CreatedUtc.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(d));

        Assert.Equal(2, d.Layers.Count);
        Assert.Equal("CMD [\"/bin/sh\"]", d.Layers[0].CreatedBy);
        Assert.Equal("ADD file:abc in /", d.Layers[1].CreatedBy);
        Assert.Null(d.Layers[1].Comment);
        Assert.Equal("9.08MB", d.Layers[1].Size);
    }

    [Fact]
    public void Inspect_and_history_quote_the_reference_and_refuse_injection()
    {
        Assert.Equal(VolumeCommands.PathPrefix + "docker image inspect --format '{{json .}}' 'nginx:1.27'", ImageCommands.Inspect("nginx:1.27"));
        Assert.Equal(VolumeCommands.PathPrefix + "sudo docker image history --no-trunc --format '{{json .}}' 'sha256:abc'", ImageCommands.History("sha256:abc", "sudo docker"));
        Assert.Throws<ArgumentException>(() => ImageCommands.Inspect("x'; rm -rf / #"));
    }

    [Fact]
    public void Prune_all_removes_every_unused_image()
        => Assert.EndsWith("image prune -f -a", ImageCommands.Prune(all: true));
}
