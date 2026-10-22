using MatDock.Core.Entities;
using MatDock.Core.Updates;
using Xunit;

namespace MatDock.Tests;

/// <summary>The update rules: which value wins (stack, environment, global, built-in), the time window, the series of a version tag, and saving a rule.</summary>
public class UpdateRulesTests
{
    private static UpdatePolicy Rule(UpdatePolicyScope scope, long id, Action<UpdatePolicy>? set = null)
    {
        var p = new UpdatePolicy { Scope = scope, ScopeId = id };
        set?.Invoke(p);
        return p;
    }

    // ---- which value wins ----

    [Fact]
    public void Without_any_rule_nothing_happens_by_itself()
    {
        var policy = UpdatePolicies.Resolve(null, null, null);

        Assert.Equal(UpdateMode.Off, policy.Mode);
        Assert.Equal(UpdateStrategy.Tag, policy.Strategy);
        Assert.True(policy.PreBackup);
        Assert.True(policy.Rollback); // what MatDock does by itself is careful
        Assert.Equal(6, policy.CheckEveryHours);
    }

    [Fact]
    public void The_narrowest_rule_that_sets_a_value_decides_it()
    {
        var global = Rule(UpdatePolicyScope.Global, 0, p => { p.Mode = UpdateMode.Notify; p.Strategy = UpdateStrategy.Minor; p.PreBackup = false; });
        var env = Rule(UpdatePolicyScope.Environment, 1, p => { p.Mode = UpdateMode.Auto; p.Rollback = false; });
        var stack = Rule(UpdatePolicyScope.Stack, 5, p => p.Strategy = UpdateStrategy.Latest);

        var policy = UpdatePolicies.Resolve(global, env, stack);

        Assert.Equal(UpdateMode.Auto, policy.Mode);          // the environment over the global one
        Assert.Equal(UpdateStrategy.Latest, policy.Strategy); // the stack over the global one
        Assert.False(policy.PreBackup);                       // only the global rule says it
        Assert.False(policy.Rollback);                        // only the environment says it
        Assert.True(policy.NotifyAvailable);                  // nobody says: the built-in value
    }

    [Fact]
    public void The_window_comes_as_a_pair_from_one_rule()
    {
        var global = Rule(UpdatePolicyScope.Global, 0, p => { p.WindowStartHour = 1; p.WindowEndHour = 5; });
        var stack = Rule(UpdatePolicyScope.Stack, 5, p => { p.WindowStartHour = 22; p.WindowEndHour = 23; });

        var own = UpdatePolicies.Resolve(global, null, stack);
        Assert.Equal((22, 23), (own.WindowStartHour, own.WindowEndHour));

        var inherited = UpdatePolicies.Resolve(global, null, Rule(UpdatePolicyScope.Stack, 5, p => p.Mode = UpdateMode.Auto));
        Assert.Equal((1, 5), (inherited.WindowStartHour, inherited.WindowEndHour));

        Assert.Null(UpdatePolicies.Resolve(null, null, null).WindowStartHour);
    }

    [Theory]
    [InlineData(2, 4, 3, true)]
    [InlineData(2, 4, 4, false)]   // the end hour is outside
    [InlineData(2, 4, 1, false)]
    [InlineData(22, 5, 23, true)]  // across midnight
    [InlineData(22, 5, 2, true)]
    [InlineData(22, 5, 12, false)]
    [InlineData(3, 3, 12, true)]   // the same hour twice: all day
    public void The_window_is_a_range_of_hours_that_may_cross_midnight(int start, int end, int hour, bool inside)
    {
        var policy = UpdatePolicies.Resolve(Rule(UpdatePolicyScope.Global, 0, p => { p.WindowStartHour = start; p.WindowEndHour = end; }), null, null);

        Assert.Equal(inside, policy.InWindow(new DateTime(2026, 10, 22, hour, 30, 0)));
    }

    [Fact]
    public void Without_a_window_any_time_is_fine()
        => Assert.True(EffectiveUpdatePolicy.Default.InWindow(new DateTime(2026, 10, 22, 13, 0, 0)));

    [Fact]
    public void A_service_has_its_own_strategy_or_the_one_of_the_rule()
    {
        var stack = Rule(UpdatePolicyScope.Stack, 5, p =>
        {
            p.Strategy = UpdateStrategy.Minor;
            p.ServiceStrategiesJson = "{\"db\":\"Pinned\",\"bad name\":\"Latest\",\"web\":\"nonsense\"}";
        });

        var policy = UpdatePolicies.Resolve(null, null, stack);

        Assert.Equal(UpdateStrategy.Pinned, policy.StrategyOf("db"));
        Assert.Equal(UpdateStrategy.Minor, policy.StrategyOf("web")); // a bad value or name is ignored
        Assert.Equal(UpdateStrategy.Minor, policy.StrategyOf("cache"));
    }

    [Fact]
    public void The_interval_stays_within_its_limits()
    {
        Assert.Equal(168, UpdatePolicies.Resolve(Rule(UpdatePolicyScope.Global, 0, p => p.CheckEveryHours = 9999), null, null).CheckEveryHours);
        Assert.Equal(1, UpdatePolicies.Resolve(Rule(UpdatePolicyScope.Global, 0, p => p.CheckEveryHours = 0), null, null).CheckEveryHours);
    }

    // ---- what a rule may say ----

    [Theory]
    [InlineData(UpdatePolicyScope.Global, 0, 3, null, null, false)]       // a start without an end
    [InlineData(UpdatePolicyScope.Global, 0, 24, 5, null, false)]
    [InlineData(UpdatePolicyScope.Global, 0, 2, 4, 0, false)]             // 0 hours between looks
    [InlineData(UpdatePolicyScope.Global, 7, null, null, null, false)]    // the global rule has no scope id
    [InlineData(UpdatePolicyScope.Stack, 0, null, null, null, false)]     // a stack rule needs its stack
    [InlineData(UpdatePolicyScope.Stack, 3, 2, 4, 12, true)]
    public void A_rule_is_checked_before_it_is_saved(UpdatePolicyScope scope, long id, int? start, int? end, int? every, bool valid)
    {
        var p = Rule(scope, id, x => { x.WindowStartHour = start; x.WindowEndHour = end; x.CheckEveryHours = every; });

        Assert.Equal(valid, UpdatePolicies.Problem(p) is null);
    }

    // ---- version tags and their series ----

    [Theory]
    [InlineData("1.2.3", "", true)]
    [InlineData("16.4-alpine", "-alpine", true)]
    [InlineData("v2.0-slim", "-slim", true)]
    [InlineData("16-alpine", null, false)]   // one number is a major line
    [InlineData("latest", null, false)]
    [InlineData("1.2.3-1", null, false)]     // a build counter is no series
    public void A_version_tag_has_a_number_and_perhaps_a_series(string tag, string? suffix, bool isVersion)
    {
        var v = TagVersion.TryParseSeries(tag);

        Assert.Equal(isVersion, v is not null);
        Assert.Equal(suffix, v?.Suffix);
    }

    private static readonly string[] Tags = ["1.2.0", "1.2.5", "1.3.0", "2.0.0", "2.1.0", "1.4.0-alpine", "1.9.0-alpine", "3.0.0-alpine", "latest", "1.10"];

    [Fact]
    public void Minor_stays_within_the_major_number_and_Latest_does_not()
    {
        Assert.Equal("1.3.0", TagVersion.Pick("1.2.0", Tags, sameMajor: true));
        Assert.Equal("2.1.0", TagVersion.Pick("1.2.0", Tags, sameMajor: false));
    }

    [Fact]
    public void A_suffix_is_its_own_series()
    {
        Assert.Equal("1.9.0-alpine", TagVersion.Pick("1.4.0-alpine", Tags, sameMajor: true));
        Assert.Equal("3.0.0-alpine", TagVersion.Pick("1.4.0-alpine", Tags, sameMajor: false));
        // the plain series never moves into the -alpine one, and the other way round
        Assert.Equal("2.1.0", TagVersion.Pick("2.0.0", Tags, sameMajor: false));
        Assert.Null(TagVersion.Pick("3.0.0-alpine", Tags, sameMajor: false));
    }

    [Fact]
    public void Nothing_newer_or_no_version_means_no_pick()
    {
        Assert.Null(TagVersion.Pick("2.1.0", Tags, sameMajor: false));
        Assert.Null(TagVersion.Pick("latest", Tags, sameMajor: false));
        Assert.Null(TagVersion.Pick("1.2.0", ["1.10"], sameMajor: false)); // another notation (two parts): not comparable
    }

    [Theory]
    [InlineData("nginx:1.2", "1.3", "nginx:1.3")]
    [InlineData("ghcr.io/o/app:1.2.0", "1.9.0", "ghcr.io/o/app:1.9.0")]
    [InlineData("localhost:5000/app:1.2.0", "2.0.0", "localhost:5000/app:2.0.0")] // the port is no tag
    [InlineData("nginx", "1.3", null)]                                           // no explicit tag
    [InlineData("nginx@sha256:abc", "1.3", null)]                                // pinned to a digest
    public void The_tag_of_an_image_is_replaced_and_nothing_else(string image, string tag, string? expected)
        => Assert.Equal(expected, ImageRef.WithTag(image, tag));

    // ---- saving a rule ----

    [Fact]
    public async Task A_rule_is_saved_changed_and_removed_when_it_says_nothing()
    {
        using var db = new TestDatabase();
        var svc = new UpdatePolicyService(db.Context);

        Assert.True((await svc.SaveAsync(Rule(UpdatePolicyScope.Environment, 1, p => { p.Mode = UpdateMode.Auto; p.WindowStartHour = 2; p.WindowEndHour = 4; }))).Ok);
        Assert.Equal(UpdateMode.Auto, (await svc.GetAsync(UpdatePolicyScope.Environment, 1))!.Mode);

        // the same scope again changes the row, it does not add one
        Assert.True((await svc.SaveAsync(Rule(UpdatePolicyScope.Environment, 1, p => p.Mode = UpdateMode.Notify))).Ok);
        var again = (await svc.GetAsync(UpdatePolicyScope.Environment, 1))!;
        Assert.Equal(UpdateMode.Notify, again.Mode);
        Assert.Null(again.WindowStartHour);
        Assert.Single(db.Context.UpdatePolicies.ToList());

        Assert.True((await svc.SaveAsync(Rule(UpdatePolicyScope.Environment, 1))).Ok); // nothing set: as the wider rule says
        Assert.Null(await svc.GetAsync(UpdatePolicyScope.Environment, 1));
    }

    [Fact]
    public async Task A_bad_rule_is_refused_and_not_saved()
    {
        using var db = new TestDatabase();
        var svc = new UpdatePolicyService(db.Context);

        var result = await svc.SaveAsync(Rule(UpdatePolicyScope.Global, 0, p => { p.WindowStartHour = 30; p.WindowEndHour = 2; }));

        Assert.False(result.Ok);
        Assert.Empty(db.Context.UpdatePolicies.ToList());
    }

    [Fact]
    public async Task The_set_of_rules_resolves_a_stack_and_tells_what_a_scope_inherits()
    {
        using var db = new TestDatabase();
        var svc = new UpdatePolicyService(db.Context);
        await svc.SaveAsync(Rule(UpdatePolicyScope.Global, 0, p => { p.Mode = UpdateMode.Notify; p.Strategy = UpdateStrategy.Minor; }));
        await svc.SaveAsync(Rule(UpdatePolicyScope.Environment, 1, p => p.Mode = UpdateMode.Auto));
        await svc.SaveAsync(Rule(UpdatePolicyScope.Stack, 8, p => p.Strategy = UpdateStrategy.Pinned));

        var set = await svc.LoadAsync();

        var stack = set.For(new Stack { Id = 8, EnvironmentId = 1 });
        Assert.Equal((UpdateMode.Auto, UpdateStrategy.Pinned), (stack.Mode, stack.Strategy));
        var other = set.For(new Stack { Id = 9, EnvironmentId = 2 });
        Assert.Equal((UpdateMode.Notify, UpdateStrategy.Minor), (other.Mode, other.Strategy));

        // what the stack's own rule falls back to: the environment's over the global one
        var inherited = set.Inherited(UpdatePolicyScope.Stack, environmentId: 1);
        Assert.Equal((UpdateMode.Auto, UpdateStrategy.Minor), (inherited.Mode, inherited.Strategy));
    }
}
