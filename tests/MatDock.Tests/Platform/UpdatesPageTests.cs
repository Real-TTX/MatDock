using System.Net;
using System.Net.Http.Json;
using MatDock.Core.Entities;
using MatDock.Core.Updates;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The Updates pages: the overview of the stacks with their rules, the form of a rule at its three scopes, and the key figure of the dashboard.</summary>
public sealed class UpdatesPageTests : IClassFixture<UpdatesPageTests.Site>
{
    private const string Compose = "services:\n  web:\n    image: localhost:1/app:1.2.0\n  db:\n    image: localhost:1/db:16\n";

    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("rules-host");
            var put = App.Api(MatDockFactory.EngineKey).PutAsJsonAsync("/api/v1/stacks/shop", new { composeYaml = Compose }).GetAwaiter().GetResult();
            put.EnsureSuccessStatusCode();
            StackId = App.Db(db => db.Stacks.Single(s => s.Name == "shop").Id);
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }
        public long StackId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public UpdatesPageTests(Site site) => _site = site;

    private async Task<HttpClient> AdminAsync()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        return browser;
    }

    private async Task<HttpResponseMessage> SaveRuleAsync(HttpClient browser, string query, params (string Key, string Value)[] fields)
    {
        var page = await (await browser.GetAsync("/Updates/Rules?" + query)).Content.ReadAsStringAsync();
        var data = fields.ToDictionary(f => f.Key, f => f.Value);
        data["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page);
        return await browser.PostAsync("/Updates/Rules?" + query, new FormUrlEncodedContent(data));
    }

    private UpdatePolicy? Row(UpdatePolicyScope scope, long id)
        => _site.App.Db(db => db.UpdatePolicies.SingleOrDefault(p => p.Scope == scope && p.ScopeId == id));

    [Fact]
    public async Task The_default_rule_is_saved_shown_again_and_taken_back_by_an_empty_form()
    {
        var browser = await AdminAsync();

        var save = await SaveRuleAsync(browser, "scope=Global", ("Input.Mode", "Notify"), ("Input.Strategy", "Minor"), ("Input.WindowStartHour", "2"), ("Input.WindowEndHour", "4"),
            ("Input.CheckEveryHours", "12"), ("Input.Rollback", "false"));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        var row = Row(UpdatePolicyScope.Global, 0)!;
        Assert.Equal((UpdateMode.Notify, UpdateStrategy.Minor, 2, 4, 12, false), (row.Mode, row.Strategy, row.WindowStartHour, row.WindowEndHour, row.CheckEveryHours, row.Rollback));
        Assert.Null(row.PreBackup); // left on "not set"

        var again = await (await browser.GetAsync("/Updates/Rules?scope=Global")).Content.ReadAsStringAsync();
        Assert.Matches("<option[^>]*value=\"Notify\"[^>]*selected|<option[^>]*selected[^>]*value=\"Notify\"", again);
        Assert.Matches("<option[^>]*value=\"Minor\"[^>]*selected|<option[^>]*selected[^>]*value=\"Minor\"", again);

        await SaveRuleAsync(browser, "scope=Global");
        Assert.Null(Row(UpdatePolicyScope.Global, 0));
    }

    [Fact]
    public async Task A_stack_rule_keeps_a_strategy_per_service_and_ignores_services_it_does_not_have()
    {
        var browser = await AdminAsync();
        var page = await (await browser.GetAsync($"/Updates/Rules?scope=Stack&id={_site.StackId}")).Content.ReadAsStringAsync();
        Assert.Contains("Input.ServiceStrategies[web]", page);
        Assert.Contains("Input.ServiceStrategies[db]", page);

        await SaveRuleAsync(browser, $"scope=Stack&id={_site.StackId}", ("Input.Mode", "Auto"), ("Input.ServiceStrategies[db]", "Pinned"), ("Input.ServiceStrategies[web]", ""),
            ("Input.ServiceStrategies[ghost]", "Latest"));

        var row = Row(UpdatePolicyScope.Stack, _site.StackId)!;
        Assert.Equal(UpdateMode.Auto, row.Mode);
        var strategies = UpdatePolicies.ServiceStrategies(row.ServiceStrategiesJson);
        Assert.Equal(new[] { "db" }, strategies.Keys);
        Assert.Equal(UpdateStrategy.Pinned, strategies["db"]);

        await SaveRuleAsync(browser, $"scope=Stack&id={_site.StackId}");
    }

    [Fact]
    public async Task A_bad_rule_shows_why_on_the_form_and_is_not_saved()
    {
        var browser = await AdminAsync();

        var response = await SaveRuleAsync(browser, $"scope=Environment&id={_site.EnvId}", ("Input.WindowStartHour", "3"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the form again ...
        Assert.Contains("start and an end hour", await response.Content.ReadAsStringAsync()); // ... with the reason
        Assert.Null(Row(UpdatePolicyScope.Environment, _site.EnvId));
    }

    [Theory]
    [InlineData("scope=Environment&id=9999")]
    [InlineData("scope=Stack&id=9999")]
    public async Task A_rule_for_something_that_is_not_there_is_not_found(string query)
    {
        var browser = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/Updates/Rules?" + query)).StatusCode);
    }

    [Fact]
    public async Task The_overview_lists_the_stack_with_the_rule_that_applies_to_it()
    {
        var browser = await AdminAsync();
        await SaveRuleAsync(browser, $"scope=Environment&id={_site.EnvId}", ("Input.Mode", "Auto"), ("Input.Strategy", "Latest"));
        try
        {
            var html = await (await browser.GetAsync("/Updates")).Content.ReadAsStringAsync();

            Assert.Contains("rules-host", html);
            Assert.Contains($"/Stacks/Update?id={_site.StackId}", html);
            Assert.Matches("mat-pill--ok\">Auto</span>\\s*<span class=\"mat-muted\">Latest", html); // from the rule of the environment
            Assert.Contains($"/Updates/Rules?scope=Stack&id={_site.StackId}", html);
        }
        finally
        {
            await SaveRuleAsync(browser, $"scope=Environment&id={_site.EnvId}");
        }
    }

    [Fact]
    public async Task Checking_all_starts_a_background_run_the_page_follows()
    {
        var browser = await AdminAsync();
        var page = await (await browser.GetAsync("/Updates")).Content.ReadAsStringAsync();

        var post = await browser.PostAsync("/Updates?handler=Check", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
        }));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("op=", SignIn.Path(post));
        var followed = await (await browser.GetAsync(SignIn.Path(post))).Content.ReadAsStringAsync();
        Assert.Contains("Checking", followed);
    }

    [Fact]
    public async Task The_dashboard_shows_the_key_figure_to_an_administrator()
    {
        var browser = await AdminAsync();

        var html = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("Updates available", html);
        Assert.Contains("href=\"/Updates\"", html);
    }
}
