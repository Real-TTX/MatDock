using System.Net;
using MatDock.Core.Activity;
using MatDock.Web.Infrastructure;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>What users and callers of the API do lands in the activity log, without any secret.</summary>
public sealed class ActivityTests(MatDockFactory site) : IClassFixture<MatDockFactory>
{
    private async Task<(HttpClient Browser, string Html)> AdminAsync(string page)
    {
        var browser = site.Browser();
        var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", login);
        return (browser, await (await browser.GetAsync(page)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saving_an_environment_is_recorded_without_its_password()
    {
        var (browser, html) = await AdminAsync("/Environments/Edit");
        var name = "act" + Guid.NewGuid().ToString("N")[..6];
        var response = await browser.PostAsync("/Environments/Edit?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["Input.Name"] = name,
            ["Input.ConnectionType"] = "Ssh",
            ["Input.Host"] = "example.org",
            ["Input.Port"] = "22",
            ["Input.Username"] = "root",
            ["Input.AuthType"] = "Password",
            ["Input.Password"] = "SuperSecret-4711",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // Saving tests the connection at once and keeps the result (the list shows it, the commands use the access found).
        Assert.Equal(MatDock.Core.Entities.EnvironmentStatus.Online, site.Db(db => db.Environments.Single(e => e.Name == name).Status));

        var entry = site.Db(db => db.ActivityEntries.Single(e => e.Target == name));
        Assert.Equal(MatDockFactory.Admin, entry.Actor);
        Assert.Equal("ui", entry.Source);
        Assert.Equal("Environments / Edit · Save", entry.Action);
        Assert.True(entry.Ok);
        Assert.False(site.Db(db => db.ActivityEntries.Any(e => (e.Target + e.Message + e.Action).Contains("SuperSecret-4711"))));

        var page = await (await browser.GetAsync("/Settings/Activity?Q=" + name)).Content.ReadAsStringAsync();
        Assert.Contains("Environments / Edit", page);
        Assert.Contains(name, page);
    }

    [Fact]
    public async Task A_call_of_the_api_is_recorded_and_one_without_a_key_is_not()
    {
        await site.Api(MatDockFactory.EngineKey).PutAsync("/api/v1/environments/local", null);
        Assert.True(site.Db(db => db.ActivityEntries.Any(e => e.Source == "api" && e.Action == "PUT /api/v1/environments/local")));

        var before = site.Db(db => db.ActivityEntries.Count());
        var refused = await site.Api("wrong-key").PutAsync("/api/v1/environments/local", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(before, site.Db(db => db.ActivityEntries.Count()));
    }

    [Theory]
    [InlineData("/Containers/Index", "Containers")]
    [InlineData("/Stacks/SyncJobs/Edit", "Stacks / SyncJobs / Edit")]
    [InlineData("/Index", "Index")]
    public void Pages_are_named_by_their_path(string page, string expected)
        => Assert.Equal(expected, ActivityPageFilter.Describe(page));
}

public class ActivityLogServiceTests
{
    [Fact]
    public async Task Searches_filters_failures_and_prunes_old_entries()
    {
        using var db = new TestDatabase();
        var log = new ActivityLogService(db.Context);
        await log.RecordAsync("anna", "ui", "Containers · Stop", "web-1", "prod", true, "Stopped.");
        await log.RecordAsync("bob", "ui", "Stacks · Deploy", "shop", "lab", false, "compose up failed\nline 2");
        await log.RecordAsync("API", "api", "PUT /api/v1/stacks/shop", null, null, true, "HTTP 200");

        var (all, total) = await log.QueryAsync(null, false, 1, 10);
        Assert.Equal(3, total);
        Assert.Equal("PUT /api/v1/stacks/shop", all[0].Action);

        var (shop, _) = await log.QueryAsync("shop", false, 1, 10);
        Assert.Equal(2, shop.Count);
        var (failed, _) = await log.QueryAsync(null, true, 1, 10);
        Assert.Equal("compose up failed line 2", Assert.Single(failed).Message);

        Assert.Equal(3, await log.PruneAsync(DateTime.UtcNow.AddMinutes(1)));
        Assert.Equal(0, (await log.QueryAsync(null, false, 1, 10)).Total);
    }
}
