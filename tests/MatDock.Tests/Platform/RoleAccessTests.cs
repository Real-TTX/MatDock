using System.Net;
using MatDock.Core.Entities;

namespace MatDock.Tests.Platform;

/// <summary>The role "User" looks around and starts/stops containers; what reaches the hosts beyond that is for admins.</summary>
public sealed class RoleAccessTests(MatDockFactory site) : IClassFixture<MatDockFactory>
{
    private async Task<HttpClient> SignInAsUserAsync()
    {
        var name = "user" + Guid.NewGuid().ToString("N")[..8];
        SignIn.AddUser(site, name, "UserPass123!", UserRole.User);
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "UserPass123!");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        return browser;
    }

    [Theory]
    [InlineData("/Environments/Edit")]
    [InlineData("/Environments/Edit?id=1")]
    [InlineData("/Environments/Migrate")]
    [InlineData("/Volumes/Create")]
    [InlineData("/Backups")]
    [InlineData("/Backups/Targets")]
    [InlineData("/Backups/Restore?id=1")]
    [InlineData("/Stacks")]
    [InlineData("/Schedules")]
    [InlineData("/Terminal")]
    [InlineData("/Containers/Files?envId=1&container=web")]
    [InlineData("/Volumes/Files?envId=1&volume=data")]
    public async Task A_user_is_refused_on_admin_pages(string path)
    {
        var browser = await SignInAsUserAsync();
        var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(response));
    }

    [Fact]
    public async Task A_user_cannot_post_to_the_environment_editor()
    {
        var browser = await SignInAsUserAsync();
        var html = await (await browser.GetAsync("/Environments")).Content.ReadAsStringAsync();
        var response = await browser.PostAsync("/Environments/Edit?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
            ["Input.Name"] = "evil",
            ["Input.ConnectionType"] = "Local",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(response));
        Assert.False(site.Db(db => db.Environments.Any(e => e.Name == "evil")));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Environments")]
    [InlineData("/Containers")]
    [InlineData("/Volumes")]
    public async Task A_user_sees_the_overview_pages(string path)
    {
        var browser = await SignInAsUserAsync();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_for_a_while()
    {
        var name = "locked" + Guid.NewGuid().ToString("N")[..8];
        SignIn.AddUser(site, name, "RightPass123!", UserRole.User);
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();

        for (var i = 0; i < Core.Auth.LoginThrottle.MaxFailuresPerAccount; i++)
        {
            await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "wrong");
        }

        // Even the right password is refused now.
        var response = await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "RightPass123!");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Too many failed sign-ins", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>The overview pages mark the part that refreshes itself (matdock.js reads it again while the page is open).</summary>
public sealed class LiveRegionTests(MatDockFactory site) : IClassFixture<MatDockFactory>
{
    [Theory]
    [InlineData("/", "live-dashboard")]
    [InlineData("/Containers", "live-containers")]
    [InlineData("/Environments", "live-environments")]
    [InlineData("/Containers/Stack?envId={env}&project=shop", "live-stack")]
    public async Task The_page_has_its_live_region(string path, string id)
    {
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", html);
        if (path.Contains("{env}", StringComparison.Ordinal))
        {
            path = path.Replace("{env}", site.AddLocalEnvironment("live" + Guid.NewGuid().ToString("N")[..6]).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        var page = await (await browser.GetAsync(path)).Content.ReadAsStringAsync();
        Assert.Contains($"id=\"{id}\" data-live=", page);
    }
}
