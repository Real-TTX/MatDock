using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Updates;
using MatDock.Tests.Platform;
using MatDock.Web.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests;

/// <summary>
/// What a look for updates costs a host: never <c>docker stats</c> (seconds on a busy Docker), and for many stacks one <c>docker ps</c> of the host and
/// one question per image to its registry. And what a page that shows the usage costs: the one reading of the host all of them share.
/// </summary>
public class UpdateLookTests
{
    private static string Container(string project, string name, string image = "localhost:5000/app:1.2.0")
        => JsonSerializer.Serialize(new
        {
            ID = "c" + project + "0000001", Names = name, Image = image, State = "running", Status = "Up 3 hours",
            Labels = $"com.docker.compose.project={project},com.docker.compose.service=web", Ports = string.Empty,
        });

    [Fact]
    public async Task A_look_for_updates_does_not_read_the_usage_of_the_host()
    {
        using var rig = new UpdateRig();

        var report = await rig.Updates.CheckStackAsync(rig.Env, "shop");

        Assert.Null(report.Error);
        Assert.Single(report.Images);
        Assert.Equal(0, rig.Count(" stats "));
    }

    [Fact]
    public async Task Checking_many_stacks_asks_the_host_once_and_the_registry_once_per_image()
    {
        // One stack alone, as the measure of what one look asks the registry.
        using var alone = new UpdateRig();
        await alone.SetRuleAsync(p => { p.Mode = UpdateMode.Notify; p.Strategy = UpdateStrategy.Latest; });
        Assert.Equal(1, await alone.Automation.CheckStacksAsync([alone.Stack], _ => { }));
        var oneLook = alone.RegistryCalls;
        Assert.True(oneLook > 0);

        using var rig = new UpdateRig();
        await rig.SetRuleAsync(p => { p.Mode = UpdateMode.Notify; p.Strategy = UpdateStrategy.Latest; });
        var others = new[] { "blog", "wiki" }.Select(name => new Stack { Name = name, EnvironmentId = rig.Env.Id, ComposeYaml = UpdateRig.Compose }).ToList();
        rig.Db.Context.Stacks.AddRange(others);
        rig.Db.Context.SaveChanges();
        rig.Hosts.Containers.Add(Container("blog", "blog-web-1"));
        rig.Hosts.Containers.Add(Container("wiki", "wiki-web-1"));
        var lines = new List<string>();

        var pending = await rig.Automation.CheckStacksAsync([rig.Stack, .. others], lines.Add);

        Assert.Equal(3, pending); // each stack got its own answer from the shared reading
        Assert.Equal(1, rig.Count(" ps -a"));
        Assert.Equal(0, rig.Count(" stats "));
        Assert.Equal(oneLook, rig.RegistryCalls); // three stacks with the same image: the registry was asked as for one
        Assert.Contains(lines, l => l.StartsWith("blog on rig: 1 update(s) (web)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_look_of_a_check_run_says_where_a_newer_version_is()
    {
        using var rig = new UpdateRig();

        await rig.Automation.CheckStacksAsync([rig.Stack], _ => { }); // the default rule: strategy Tag, the newer version is only a hint

        var look = rig.Cache.Report(rig.Env.Id, "shop");
        Assert.True(look?.Report.NewerTags);
        Assert.Equal("2.0.0", look?.Report.Images.Single().NewerTag);
    }

    [Fact]
    public async Task Another_strategy_forgets_the_plans_and_another_mode_does_not()
    {
        using var db = new TestDatabase();
        var cache = new UpdateCheckCache();
        var policies = new UpdatePolicyService(db.Context, cache);
        void Plan() => cache.SetPlan(1, "shop", new StackUpdatePlan([new PlannedUpdate("web", "app:1", "app:2", PlannedKind.Version)], null));

        Plan();
        await policies.SaveAsync(new UpdatePolicy { Scope = UpdatePolicyScope.Global, ScopeId = 0, Mode = UpdateMode.Notify });
        Assert.NotNull(cache.Plan(1, "shop")); // the mode does not change what would be updated

        await policies.SaveAsync(new UpdatePolicy { Scope = UpdatePolicyScope.Global, ScopeId = 0, Mode = UpdateMode.Notify, Strategy = UpdateStrategy.Latest });
        Assert.Null(cache.Plan(1, "shop"));

        Plan();
        await policies.SaveAsync(new UpdatePolicy { Scope = UpdatePolicyScope.Global, ScopeId = 0 }); // the rule is taken back: its strategy with it
        Assert.Null(cache.Plan(1, "shop"));
    }

    [Fact]
    public async Task The_list_a_stack_and_a_container_share_one_reading_of_the_usage()
    {
        using var db = new TestDatabase();
        var options = Options.Create(new MatDockOptions());
        var environments = new EnvironmentService(db.Context, new FakeSecretProtector(), new FakeConnectionService(), options);
        var hosts = new FakeHostSessionFactory();
        hosts.Containers.Add(Container("shop", "shop-web-1"));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var containers = new ContainerService(hosts, environments, options, NullLogger<ContainerService>.Instance, cache: cache);
        var env = new DockerEnvironment { Name = "server2", Host = "server2", Username = "me", IsEnabled = true };
        db.Context.Environments.Add(env);
        db.Context.SaveChanges();
        int Stats() => hosts.Commands.Count(c => c.Contains(" stats --no-stream", StringComparison.Ordinal));

        Assert.Single(await containers.ListByProjectAsync(env, "shop")); // a backup, a look for updates, the health gate
        Assert.Equal(0, Stats());

        await containers.ListAsync(env);
        await containers.ListByProjectAsync(env, "shop", includeStats: true); // the page of the stack
        var (container, _) = await containers.GetDetailAsync(env, "shop-web-1"); // the page of the container

        Assert.NotNull(container);
        Assert.Equal(1, Stats());
    }

    [Fact]
    public void A_time_is_shown_as_the_clock_of_the_user_says()
    {
        var stored = new DateTime(2026, 11, 13, 23, 31, 0); // as the database gives it back: UTC, the kind not said
        try
        {
            UserClock.Use("Europe/Berlin");
            Assert.Equal(new DateTime(2026, 11, 14, 0, 31, 0), stored.ToUserTime());
            Assert.Equal(new DateTime(2026, 7, 1, 14, 0, 0), new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc).ToUserTime()); // summer time
            Assert.Equal(TimeSpan.FromHours(1), new DateTimeOffset(stored, TimeSpan.Zero).ToUserTime().Offset);

            UserClock.Use("Europe%2FBerlin"); // as the browser writes it into the cookie
            Assert.Equal(new DateTime(2026, 11, 14, 0, 31, 0), stored.ToUserTime());

            UserClock.Use("Not/AZone");
            Assert.Equal(TimeZoneInfo.Local, UserClock.Zone);
            UserClock.Use(new string('x', 200));
            Assert.Equal(TimeZoneInfo.Local, UserClock.Zone);
        }
        finally
        {
            UserClock.Use(null);
        }
    }
}
