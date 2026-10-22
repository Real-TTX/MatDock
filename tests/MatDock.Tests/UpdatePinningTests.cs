using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

public class UpdatePinningTests
{
    // ---- version tags ----

    [Theory]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("v1.2", "v1.2", true)]
    [InlineData("2024.10.1", "2024.10.1", true)]
    [InlineData("latest", null, false)]
    [InlineData("16-alpine", null, false)]
    [InlineData("1.2.3-rc1", null, false)]
    [InlineData("1", null, false)] // one number is a major line, not a version
    [InlineData("", null, false)]
    public void Only_plain_version_tags_are_versions(string tag, string? expected, bool isVersion)
    {
        var v = TagVersion.TryParse(tag);
        Assert.Equal(isVersion, v is not null);
        Assert.Equal(expected, v?.Tag);
    }

    [Fact]
    public void Newest_takes_the_highest_tag_written_the_same_way()
    {
        var tags = new[] { "1.2.3", "1.2.10", "1.3.0", "2.0.0", "v9.9.9", "1.3", "1.9.0-rc1", "latest", "1.4.0.1" };
        Assert.Equal("2.0.0", TagVersion.Newest("1.2.3", tags)); // numeric order (1.2.10 > 1.2.3), v9.9.9 / 1.3 / 1.4.0.1 are other shapes
        Assert.Equal("v9.9.9", TagVersion.Newest("v1.0.0", tags));
        Assert.Null(TagVersion.Newest("2.0.0", tags));      // already the newest
        Assert.Null(TagVersion.Newest("latest", tags));      // not a version: no suggestion
        Assert.Null(TagVersion.Newest("1.2.3", new[] { "1.2.3", "1.2.2" }));
    }

    // ---- pinned services in the commands ----

    [Fact]
    public void Update_without_pins_pulls_everything()
    {
        var cmd = StackCommands.Update("docker", "media");
        Assert.Contains("docker compose -p \"$name\" pull;", cmd);
        Assert.DoesNotContain("config --services", cmd);
    }

    [Fact]
    public void Update_with_pins_pulls_only_the_services_that_are_not_pinned()
    {
        var cmd = StackCommands.Update("docker", "media", new[] { "db", "cache" });
        Assert.Contains("pins=\" db cache \"", cmd);
        Assert.Contains("docker compose -p \"$name\" config --services", cmd);
        Assert.Contains("docker compose -p \"$name\" pull $svcs", cmd);
        Assert.Contains("docker compose -p \"$name\" up -d", cmd); // the stack is still brought up

        // The body sits in sh -c '…': a single quote would end it.
        var start = cmd.IndexOf("sh -c '", StringComparison.Ordinal) + "sh -c '".Length;
        var end = cmd.LastIndexOf("' 2>&1", StringComparison.Ordinal);
        Assert.DoesNotContain("'", cmd.Substring(start, end - start));
    }

    [Fact]
    public void GitSync_with_pins_uses_the_compose_file_argument()
    {
        var cmd = StackCommands.GitSync("docker", "media", "deploy/compose.yml", pull: true, pinned: new[] { "db" });
        Assert.Contains("docker compose -p \"$1\" -f \"$2\" config --services", cmd);
        Assert.Contains("docker compose -p \"$1\" -f \"$2\" pull $svcs", cmd);
        // No pull requested: pins change nothing.
        Assert.DoesNotContain("config --services", StackCommands.GitSync("docker", "media", "c.yml", pull: false, pinned: new[] { "db" }));
    }

    [Theory]
    [InlineData("db; rm -rf /")]
    [InlineData("a b")]
    [InlineData("$(id)")]
    [InlineData("a'b")]
    public void A_bad_pinned_name_never_reaches_the_command(string name)
        => Assert.Throws<ArgumentException>(() => StackCommands.Update("docker", "media", new[] { name }));

    // ---- pinning a service ----

    private static StackService Service(TestDatabase db)
        => new(db.Context, new NoHostSessionFactory(), null!, null!, null!, null!, new NoopScheduleEventBus(),
            Options.Create(new MatDockOptions()), NullLogger<StackService>.Instance);

    private static async Task<long> AddStackAsync(TestDatabase db)
    {
        var stack = new Stack { Name = "media", EnvironmentId = 1, ComposeYaml = "services: {}" };
        db.Context.Stacks.Add(stack);
        await db.Context.SaveChangesAsync();
        return stack.Id;
    }

    [Fact]
    public async Task Pinning_and_unpinning_a_service_is_remembered()
    {
        using var db = new TestDatabase();
        var id = await AddStackAsync(db);
        var svc = Service(db);

        Assert.True((await svc.SetPinnedAsync(id, "web", true)).Ok);
        Assert.True((await svc.SetPinnedAsync(id, "db", true)).Ok);
        Assert.Equal(new[] { "db", "web" }, StackService.PinnedOf((await svc.GetAsync(id))!));

        Assert.True((await svc.SetPinnedAsync(id, "web", false)).Ok);
        Assert.Equal(new[] { "db" }, StackService.PinnedOf((await svc.GetAsync(id))!));

        Assert.True((await svc.SetPinnedAsync(id, "db", false)).Ok);
        Assert.Null((await svc.GetAsync(id))!.PinnedServices); // nothing pinned = no value
    }

    [Fact]
    public async Task Pinning_twice_or_a_bad_name_or_an_unknown_stack_is_refused_or_harmless()
    {
        using var db = new TestDatabase();
        var id = await AddStackAsync(db);
        var svc = Service(db);

        await svc.SetPinnedAsync(id, "web", true);
        var again = await svc.SetPinnedAsync(id, "web", true);
        Assert.True(again.Ok);
        Assert.Contains("already", again.Message);
        Assert.Equal(new[] { "web" }, StackService.PinnedOf((await svc.GetAsync(id))!));

        Assert.False((await svc.SetPinnedAsync(id, "bad name;", true)).Ok);
        Assert.False((await svc.SetPinnedAsync(9999, "web", true)).Ok);
    }

    [Fact]
    public void PinnedOf_drops_invalid_and_duplicate_names_from_a_stored_value()
    {
        var stack = new Stack { PinnedServices = "web, db,web,bad name,$(id),," };
        Assert.Equal(new[] { "web", "db" }, StackService.PinnedOf(stack));
    }

    // ---- what changed in a run ----

    [Fact]
    public void DiffChanges_lists_only_services_whose_image_id_changed()
    {
        var before = new List<(string, string, string)> { ("web", "nginx:1", "sha256:aaaaaaaaaaaaaaaa"), ("db", "pg:16", "sha256:bbbbbbbbbbbbbbbb") };
        var after = new List<(string, string, string)> { ("web", "nginx:1", "sha256:cccccccccccccccc"), ("db", "pg:16", "sha256:bbbbbbbbbbbbbbbb"), ("new", "x:1", "sha256:dddd") };

        var changes = UpdateService.DiffChanges(before, after);
        var c = Assert.Single(changes);
        Assert.Equal(("web", "nginx:1", "aaaaaaaaaaaa", "cccccccccccc"), (c.Service, c.Image, c.Before, c.After));
    }

    [Fact]
    public void DiffChanges_is_empty_when_a_reading_failed()
    {
        var some = new List<(string, string, string)> { ("web", "nginx:1", "sha256:aaaa") };
        Assert.Empty(UpdateService.DiffChanges(null, some));
        Assert.Empty(UpdateService.DiffChanges(some, null));
        Assert.Empty(UpdateService.DiffChanges(some, some));
    }
}
