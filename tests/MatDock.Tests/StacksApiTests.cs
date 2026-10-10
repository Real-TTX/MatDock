using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Stacks;
using MatDock.Web.Api;
using Xunit;

namespace MatDock.Tests;

public class StacksApiTests
{
    private const string Compose = "services:\n  web:\n    image: nginx:alpine\n";

    // ---- body validation --------------------------------------------------------------------

    [Fact]
    public void Validate_accepts_a_compose_mapping()
        => Assert.Null(StacksApi.Validate(new UpsertStackBody { ComposeYaml = Compose, ManagedBy = "mattiverse", Deploy = true, Pull = true }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public void Validate_requires_compose(string? yaml)
        => Assert.Contains("composeYaml", StacksApi.Validate(new UpsertStackBody { ComposeYaml = yaml }));

    [Fact]
    public void Validate_rejects_a_missing_body() => Assert.NotNull(StacksApi.Validate(null));

    [Theory]
    [InlineData("just a string")]
    [InlineData("- a\n- b\n")]
    [InlineData("services: [1,")]
    public void Validate_rejects_yaml_that_is_no_mapping_or_broken(string yaml)
        => Assert.NotNull(StacksApi.Validate(new UpsertStackBody { ComposeYaml = yaml }));

    [Fact]
    public void Validate_limits_managedBy_and_needs_deploy_for_pull()
    {
        Assert.NotNull(StacksApi.Validate(new UpsertStackBody { ComposeYaml = Compose, ManagedBy = new string('x', 101) }));
        Assert.NotNull(StacksApi.Validate(new UpsertStackBody { ComposeYaml = Compose, Pull = true }));
    }

    [Theory]
    [InlineData("mattiverse-nextcloud", true)]
    [InlineData("a", true)]
    [InlineData("Mattiverse", false)]
    [InlineData("-x", false)]
    [InlineData("a.b", false)]
    [InlineData("../etc", false)]
    public void Stack_names_follow_the_compose_rule(string name, bool valid)
        => Assert.Equal(valid, StackCommands.IsValidName(name));

    [Fact]
    public void Stack_names_are_at_most_63_characters()
    {
        Assert.True(StackCommands.IsValidName(new string('a', 63)));
        Assert.False(StackCommands.IsValidName(new string('a', 64)));
    }

    [Fact]
    public void LastLine_returns_the_last_non_empty_line()
    {
        Assert.Equal("Error: no such image", StacksApi.LastLine(" Pulling\nError: no such image\n\n"));
        Assert.Equal("unknown error", StacksApi.LastLine("  \n"));
    }

    // ---- upsert (StackService.UpsertInlineAsync, what PUT /stacks/{name} calls) ---------------

    [Fact]
    public async Task Upsert_creates_then_repeating_it_changes_nothing()
    {
        using var db = new TestDatabase();
        var stacks = TestServices.Stacks(db);

        var first = await stacks.UpsertInlineAsync(1, "mattiverse-app", Compose, "A=1\n", "mattiverse");
        Assert.True(first.Ok);
        Assert.True(first.Created);

        var second = await stacks.UpsertInlineAsync(1, "mattiverse-app", Compose, "A=1\n", "mattiverse");
        Assert.True(second.Ok);
        Assert.False(second.Created);

        using var read = db.NewContext();
        var row = Assert.Single(read.Stacks);
        Assert.Equal(UpdateState.Created, row.UpdateState); // no write happened the second time
        Assert.Equal("mattiverse", row.ManagedBy);
        Assert.Equal("A=1\n", row.EnvContent);
    }

    [Fact]
    public async Task Upsert_replaces_compose_and_env_and_null_managedBy_keeps_the_owner()
    {
        using var db = new TestDatabase();
        var stacks = TestServices.Stacks(db);
        await stacks.UpsertInlineAsync(1, "app", Compose, "A=1", "mattiverse");

        var changed = Compose + "    restart: always\n";
        var (ok, _, stack, created) = await stacks.UpsertInlineAsync(1, "app", changed, null, null);

        Assert.True(ok);
        Assert.False(created);
        Assert.Equal(changed, stack!.ComposeYaml);
        Assert.Null(stack.EnvContent);          // PUT replaces the .env too
        Assert.Equal("mattiverse", stack.ManagedBy);

        await stacks.UpsertInlineAsync(1, "app", changed, null, string.Empty);
        Assert.Null((await stacks.GetByNameAsync(1, "app"))!.ManagedBy); // "" clears it
    }

    [Fact]
    public async Task Upsert_is_per_environment()
    {
        using var db = new TestDatabase();
        var stacks = TestServices.Stacks(db);

        Assert.True((await stacks.UpsertInlineAsync(1, "app", Compose, null, null)).Created);
        Assert.True((await stacks.UpsertInlineAsync(2, "app", Compose, null, null)).Created);
        using var read = db.NewContext();
        Assert.Equal(2, read.Stacks.Count());
    }

    [Fact]
    public async Task Upsert_rejects_invalid_names_and_git_stacks()
    {
        using var db = new TestDatabase();
        var stacks = TestServices.Stacks(db);

        var invalid = await stacks.UpsertInlineAsync(1, "Not Valid", Compose, null, null);
        Assert.False(invalid.Ok);
        Assert.Contains("Invalid stack name", invalid.Message);

        await stacks.CreateAsync(new StackInput { Name = "git-app", EnvironmentId = 1, GitRepoUrl = "https://example.com/r.git" });
        var git = await stacks.UpsertInlineAsync(1, "git-app", Compose, null, "mattiverse");
        Assert.False(git.Ok);
        Assert.True(git.Stack!.IsGitBacked);
    }

    [Fact]
    public async Task Editor_save_keeps_managedBy()
    {
        using var db = new TestDatabase();
        var stacks = TestServices.Stacks(db);
        var (_, _, stack, _) = await stacks.UpsertInlineAsync(1, "app", Compose, null, "mattiverse");

        // The Stacks editor builds a StackInput without ManagedBy.
        await stacks.UpdateAsync(stack!.Id, new StackInput { Name = "app", EnvironmentId = 1, ComposeYaml = Compose, EnvContent = "X=1" });

        Assert.Equal("mattiverse", (await stacks.GetAsync(stack.Id))!.ManagedBy);
    }

    // ---- stack state --------------------------------------------------------------------------

    private static DockerContainer C(string state, string status, string service = "web", string project = "app")
        => new() { Id = Guid.NewGuid().ToString("N")[..12], Name = service, Service = service, Project = project, State = state, Status = status, Image = "nginx" };

    [Fact]
    public void State_is_missing_without_containers() => Assert.Equal("missing", StackViews.StateOf([]));

    [Fact]
    public void State_reflects_running_stopped_partial_and_unhealthy()
    {
        Assert.Equal("running", StackViews.StateOf([C("running", "Up 2 hours"), C("running", "Up 2 hours (healthy)", "db")]));
        Assert.Equal("stopped", StackViews.StateOf([C("exited", "Exited (0) 1 minute ago")]));
        Assert.Equal("partial", StackViews.StateOf([C("running", "Up 1 hour"), C("exited", "Exited (1) 5 minutes ago", "db")]));
        Assert.Equal("unhealthy", StackViews.StateOf([C("running", "Up 1 hour (unhealthy)")]));
        // A one-shot init container that finished cleanly does not make the stack partial.
        Assert.Equal("running", StackViews.StateOf([C("running", "Up 1 hour"), C("exited", "Exited (0) 1 hour ago", "init")]));
    }

    [Fact]
    public void List_merges_managed_and_discovered_stacks_of_one_environment()
    {
        var managed = new[]
        {
            new Stack { Name = "app", EnvironmentId = 1, ManagedBy = "mattiverse" },
            new Stack { Name = "idle", EnvironmentId = 1 },
            new Stack { Name = "elsewhere", EnvironmentId = 2 },
        };
        var containers = new[] { C("running", "Up", project: "app"), C("running", "Up", project: "other") };

        var rows = StackViews.List(1, managed, containers);

        Assert.Equal(["app", "idle", "other"], rows.Select(r => r.Name));
        Assert.Equal("running", rows[0].State);
        Assert.Equal("mattiverse", rows[0].ManagedBy);
        Assert.Equal("missing", rows[1].State);
        Assert.False(rows[2].Managed);
    }
}
