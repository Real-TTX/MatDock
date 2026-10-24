using System.Net;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Users;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>The role "Viewer" sees what a user sees and changes nothing: no buttons, and a POST it sends anyway is refused before it reaches Docker.</summary>
public sealed class ViewerRoleTests : IClassFixture<ViewerRoleTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            EnvId = App.AddLocalEnvironment("viewer-host");
            App.Hosts.Containers.Add(JsonSerializer.Serialize(new { ID = "runnerid0001", Names = "runner", Image = "nginx", State = "running", Status = "Up 3 hours", Labels = string.Empty, Ports = string.Empty }));
        }

        public MatDockFactory App { get; }
        public long EnvId { get; }

        public void Dispose() => App.Dispose();
    }

    private readonly Site _site;

    public ViewerRoleTests(Site site) => _site = site;

    private async Task<HttpClient> SignInAsync(UserRole role)
    {
        var name = role.ToString().ToLowerInvariant() + Guid.NewGuid().ToString("N")[..8];
        SignIn.AddUser(_site.App, name, "UserPass123!", role);
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, name, "UserPass123!")).StatusCode);
        return browser;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Environments")]
    [InlineData("/Containers")]
    [InlineData("/Volumes")]
    [InlineData("/Images")]
    [InlineData("/Networks")]
    public async Task A_viewer_sees_the_overview_pages(string path)
    {
        var browser = await SignInAsync(UserRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task A_viewer_gets_no_buttons_a_user_does()
    {
        var viewer = await (await (await SignInAsync(UserRole.Viewer)).GetAsync("/Containers")).Content.ReadAsStringAsync();
        Assert.Contains("runner", viewer);
        Assert.Contains("/Containers/Logs?EnvId=", viewer);
        Assert.DoesNotContain("handler=Action", viewer);

        var user = await (await (await SignInAsync(UserRole.User)).GetAsync("/Containers")).Content.ReadAsStringAsync();
        Assert.Contains("handler=Action", user);

        var detail = await (await (await SignInAsync(UserRole.Viewer)).GetAsync($"/Containers/ContainerDetail?envId={_site.EnvId}&id=runnerid0001")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("handler=Action", detail);
    }

    [Fact]
    public async Task A_post_of_a_viewer_is_refused_and_never_reaches_docker()
    {
        var browser = await SignInAsync(UserRole.Viewer);
        var page = await (await browser.GetAsync("/Containers")).Content.ReadAsStringAsync();
        _site.App.Hosts.Commands.Clear();

        var response = await browser.PostAsync("/Containers?handler=Action", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page),
            ["envId"] = _site.EnvId.ToString(),
            ["id"] = "runnerid0001",
            ["name"] = "runner",
            ["action"] = "Stop",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(response));
        Assert.DoesNotContain(_site.App.Hosts.Commands, c => c.Contains(" stop ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_viewer_signs_out_and_stays_away_from_admin_pages()
    {
        var browser = await SignInAsync(UserRole.Viewer);
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(await browser.GetAsync("/Settings/SelfBackup")));
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(await browser.GetAsync("/Stacks")));

        var page = await (await browser.GetAsync("/Containers")).Content.ReadAsStringAsync();
        var signOut = await browser.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(page) }));
        Assert.DoesNotContain("AccessDenied", SignIn.Path(signOut));
    }

    [Fact]
    public async Task A_viewer_can_be_limited_to_environments_an_admin_never()
    {
        var viewer = SignIn.AddUser(_site.App, "limited-viewer", "UserPass123!", UserRole.Viewer);
        var admin = SignIn.AddUser(_site.App, "unlimited-admin", "UserPass123!", UserRole.Admin);
        using var scope = _site.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();

        Assert.True(await users.SetEnvironmentAccessAsync(viewer, true, [_site.EnvId]));
        Assert.True(await users.SetEnvironmentAccessAsync(admin, true, [_site.EnvId]));

        Assert.True(SignIn.UserOf(_site.App, viewer).RestrictEnvironments);
        Assert.False(SignIn.UserOf(_site.App, admin).RestrictEnvironments);
    }

    [Fact]
    public async Task The_user_editor_offers_the_role()
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await SignIn.PostLocalFormAsync(browser, "/Account/Login", html);
        var editor = await (await browser.GetAsync("/Users/Edit")).Content.ReadAsStringAsync();
        Assert.Contains("<option value=\"Viewer\">", editor);
        Assert.Contains("data-show-value=\"User,Viewer\"", editor);
    }
}
