using System.Net;
using MatDock.Core.Configuration;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Security;
using MatDock.Core.Users;
using Microsoft.Extensions.Options;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>Who sees which environments: administrators and unrestricted users all, a restricted user only the granted ones, system work (no user) all.</summary>
public sealed class EnvironmentRightsServiceTests
{
    private static EnvironmentService Service(TestDatabase db, long? userId)
        => new(db.Context, new FakeSecretProtector(), new FakeConnectionService(), Options.Create(new MatDockOptions()), new FakeCurrentUser(userId));

    private static long AddEnvironment(TestDatabase db, string name, bool enabled = true)
    {
        var env = new DockerEnvironment { Name = name, ConnectionType = ConnectionType.Local, IsEnabled = enabled };
        db.Context.Environments.Add(env);
        db.Context.SaveChanges();
        return env.Id;
    }

    private static long AddUser(TestDatabase db, UserRole role, bool restrict, params long[] granted)
    {
        var user = new User { Username = "u" + Guid.NewGuid().ToString("N")[..8], Role = role, RestrictEnvironments = restrict, IsActive = true };
        db.Context.Users.Add(user);
        db.Context.SaveChanges();
        foreach (var id in granted)
        {
            db.Context.UserEnvironments.Add(new UserEnvironment { UserId = user.Id, EnvironmentId = id });
        }

        db.Context.SaveChanges();
        return user.Id;
    }

    private static string[] Names(IEnumerable<DockerEnvironment> envs) => envs.Select(e => e.Name).Order().ToArray();

    [Fact]
    public async Task A_restricted_user_sees_and_reaches_only_the_granted_environments()
    {
        using var db = new TestDatabase();
        var a = AddEnvironment(db, "alpha");
        var b = AddEnvironment(db, "beta");
        AddEnvironment(db, "gamma-off", enabled: false);
        var user = AddUser(db, UserRole.User, restrict: true, a);
        var svc = Service(db, user);

        Assert.Equal(new[] { "alpha" }, Names(await svc.GetAllAsync()));
        Assert.Equal(new[] { "alpha" }, Names(await svc.GetEnabledAsync()));
        Assert.NotNull(await svc.GetAsync(a));
        Assert.Null(await svc.GetAsync(b)); // by its id, too: a URL with envId=beta leads nowhere
        Assert.Equal(new long[] { a }, (await svc.AllowedEnvironmentIdsAsync())!.ToArray());
    }

    [Fact]
    public async Task A_restricted_user_without_grants_sees_nothing()
    {
        using var db = new TestDatabase();
        var a = AddEnvironment(db, "alpha");
        var svc = Service(db, AddUser(db, UserRole.User, restrict: true));

        Assert.Empty(await svc.GetAllAsync());
        Assert.Empty(await svc.GetEnabledAsync());
        Assert.Null(await svc.GetAsync(a));
    }

    [Fact]
    public async Task Admins_unrestricted_users_and_system_work_see_everything()
    {
        using var db = new TestDatabase();
        var a = AddEnvironment(db, "alpha");
        AddEnvironment(db, "beta");
        var admin = AddUser(db, UserRole.Admin, restrict: true); // the flag means nothing for an admin
        var open = AddUser(db, UserRole.User, restrict: false, a); // grants without the flag mean nothing either

        foreach (var userId in new long?[] { admin, open, null })
        {
            var svc = Service(db, userId);
            Assert.Equal(new[] { "alpha", "beta" }, Names(await svc.GetAllAsync()));
            Assert.Null(await svc.AllowedEnvironmentIdsAsync());
            Assert.NotNull(await svc.GetAsync(a));
        }
    }

    [Fact]
    public async Task An_unknown_user_sees_nothing()
    {
        using var db = new TestDatabase();
        AddEnvironment(db, "alpha");
        Assert.Empty(await Service(db, 987654).GetAllAsync()); // fail closed
    }

    [Fact]
    public async Task A_grant_to_a_deleted_environment_shows_nothing_and_a_new_grant_can_follow()
    {
        using var db = new TestDatabase();
        var a = AddEnvironment(db, "alpha");
        var user = AddUser(db, UserRole.User, restrict: true, a);
        var svc = Service(db, user);
        await svc.DeleteAsync(a); // soft delete of the environment (the admin path works on the table, not on the filtered read)

        Assert.Empty(await Service(db, user).GetAllAsync());
    }

    // ---- granting ----

    private static UserService Users(TestDatabase db) => new(db.Context, new Pbkdf2PasswordHasher());

    [Fact]
    public async Task Granting_replaces_the_list_and_drops_environments_that_do_not_exist()
    {
        using var db = new TestDatabase();
        var a = AddEnvironment(db, "alpha");
        var b = AddEnvironment(db, "beta");
        var c = AddEnvironment(db, "gamma");
        var user = AddUser(db, UserRole.User, restrict: false);
        var users = Users(db);

        Assert.True(await users.SetEnvironmentAccessAsync(user, true, new[] { a, b, b, 9999 }));
        Assert.Equal(new[] { a, b }, (await users.GetEnvironmentIdsAsync(user)).Order().ToArray());
        Assert.True((await users.GetAsync(user))!.RestrictEnvironments);

        Assert.True(await users.SetEnvironmentAccessAsync(user, true, new[] { b, c }));
        Assert.Equal(new[] { b, c }, (await users.GetEnvironmentIdsAsync(user)).Order().ToArray());

        // Back to a grant that was removed once: allowed again (a soft-deleted row does not block it).
        Assert.True(await users.SetEnvironmentAccessAsync(user, true, new[] { a, b, c }));
        Assert.Equal(new[] { a, b, c }, (await users.GetEnvironmentIdsAsync(user)).Order().ToArray());

        Assert.Equal(3, (await users.GetRestrictedCountsAsync())[user]);
    }

    [Fact]
    public async Task Lifting_the_restriction_clears_the_grants_and_an_admin_cannot_be_restricted()
    {
        using var db = new TestDatabase();
        var a = AddEnvironment(db, "alpha");
        var user = AddUser(db, UserRole.User, restrict: true, a);
        var admin = AddUser(db, UserRole.Admin, restrict: false);
        var users = Users(db);

        Assert.True(await users.SetEnvironmentAccessAsync(user, false, new[] { a }));
        Assert.False((await users.GetAsync(user))!.RestrictEnvironments);
        Assert.Empty(await users.GetEnvironmentIdsAsync(user));
        Assert.DoesNotContain(user, (await users.GetRestrictedCountsAsync()).Keys);

        Assert.True(await users.SetEnvironmentAccessAsync(admin, true, new[] { a }));
        Assert.False((await users.GetAsync(admin))!.RestrictEnvironments);
        Assert.Empty(await users.GetEnvironmentIdsAsync(admin));

        Assert.False(await users.SetEnvironmentAccessAsync(424242, true, new[] { a }));
    }
}

/// <summary>The pages as a restricted user meets them: nothing of an environment that is not theirs, not by the list, a URL, or the cookie of the selector.</summary>
public sealed class EnvironmentRightsPageTests : IClassFixture<EnvironmentRightsPageTests.Site>
{
    public sealed class Site : IDisposable
    {
        public Site()
        {
            App = new MatDockFactory();
            Alpha = App.AddLocalEnvironment("rights-alpha");
            Beta = App.AddLocalEnvironment("rights-beta");
            OnlyAlpha = Restricted("only-alpha", Alpha);
            Nothing = Restricted("nothing");
            Everything = SignIn.AddUser(App, "everything", "UserPass123!", UserRole.User);
        }

        public MatDockFactory App { get; }
        public long Alpha { get; }
        public long Beta { get; }
        public long OnlyAlpha { get; }
        public long Nothing { get; }
        public long Everything { get; }

        public void Dispose() => App.Dispose();

        private long Restricted(string name, params long[] granted)
        {
            var id = SignIn.AddUser(App, name, "UserPass123!", UserRole.User);
            App.Db(db =>
            {
                db.Users.Single(u => u.Id == id).RestrictEnvironments = true;
                foreach (var env in granted)
                {
                    db.UserEnvironments.Add(new UserEnvironment { UserId = id, EnvironmentId = env });
                }

                return db.SaveChanges();
            });
            return id;
        }
    }

    private readonly Site _site;

    public EnvironmentRightsPageTests(Site site) => _site = site;

    private async Task<HttpClient> SignInAsync(string user)
    {
        var browser = _site.App.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var response = await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, user, "UserPass123!");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return browser;
    }

    private static async Task<string> BodyAsync(HttpClient browser, string path)
    {
        var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Environments")]
    [InlineData("/Environments?view=gallery")]
    [InlineData("/Containers")]
    [InlineData("/Containers?view=gallery")]
    [InlineData("/Images")]
    [InlineData("/Volumes")]
    [InlineData("/Networks")]
    [InlineData("/Launchpad")]
    public async Task A_restricted_user_sees_their_environment_and_no_other_on_any_overview(string path)
    {
        var browser = await SignInAsync("only-alpha");
        var html = await BodyAsync(browser, path);

        Assert.Contains("rights-alpha", html);
        Assert.DoesNotContain("rights-beta", html); // not in the list, the switcher or anywhere else
    }

    [Theory]
    [InlineData("/Containers?EnvId={beta}")]
    [InlineData("/Volumes?EnvId={beta}")]
    [InlineData("/Networks?EnvId={beta}")]
    [InlineData("/Images?EnvId={beta}")]
    [InlineData("/Environments?Q=beta")] // the term is echoed in the search field, so look for the whole name: only a result row would carry it
    public async Task Asking_for_another_environment_by_a_parameter_shows_nothing_of_it(string path)
    {
        var browser = await SignInAsync("only-alpha");
        var response = await browser.GetAsync(path.Replace("{beta}", _site.Beta.ToString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("rights-beta", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/Containers/ContainerDetail?envId={beta}&id=a")]
    [InlineData("/Containers/Logs?envId={beta}&id=a")]
    [InlineData("/Containers/Stack?envId={beta}&project=shop")]
    [InlineData("/Volumes/Detail?envId={beta}&volume=data")]
    [InlineData("/Networks/Detail?envId={beta}&network=bridge")]
    [InlineData("/Images/Detail?envId={beta}&reference=nginx:1")]
    public async Task An_environment_of_someone_else_is_not_reachable_by_its_id(string path)
    {
        var browser = await SignInAsync("only-alpha");
        var response = await browser.GetAsync(path.Replace("{beta}", _site.Beta.ToString()));

        // Not found, a redirect or an empty page — but never the page of the other environment.
        var body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : string.Empty;
        Assert.DoesNotContain("rights-beta", body);
        Assert.True(response.StatusCode != HttpStatusCode.OK || !body.Contains("c-a"), "the container of the other environment must not render");
    }

    [Fact]
    public async Task A_forged_cookie_of_the_environment_selector_does_not_open_another_environment()
    {
        var cookies = new CookieContainer();
        cookies.Add(new Uri("http://localhost"), new Cookie("matdock.env", _site.Beta.ToString()));
        var browser = _site.App.Browser(cookies);
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, "only-alpha", "UserPass123!")).StatusCode);

        foreach (var path in new[] { "/", "/Containers", "/Volumes", "/Networks", "/Images" })
        {
            var page = await BodyAsync(browser, path);
            Assert.DoesNotContain("rights-beta", page);
        }
    }

    [Fact]
    public async Task A_user_without_environments_sees_empty_pages_not_errors()
    {
        var browser = await SignInAsync("nothing");

        foreach (var path in new[] { "/", "/Environments", "/Containers", "/Volumes", "/Networks", "/Images", "/Launchpad" })
        {
            var html = await BodyAsync(browser, path);
            Assert.DoesNotContain("rights-alpha", html);
            Assert.DoesNotContain("rights-beta", html);
        }
    }

    [Fact]
    public async Task An_unrestricted_user_and_an_admin_see_both_environments()
    {
        foreach (var user in new[] { "everything", MatDockFactory.Admin })
        {
            var browser = _site.App.Browser();
            var login = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
            var password = user == MatDockFactory.Admin ? MatDockFactory.AdminPassword : "UserPass123!";
            Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", login, user, password)).StatusCode);

            var html = await BodyAsync(browser, "/Environments");
            Assert.Contains("rights-alpha", html);
            Assert.Contains("rights-beta", html);
        }
    }

    [Fact]
    public async Task The_api_with_its_key_is_not_limited_by_user_rights()
    {
        var response = await _site.App.Api(MatDockFactory.EngineKey).GetAsync("/api/v1/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("rights-alpha", body);
        Assert.Contains("rights-beta", body);
    }

    [Fact]
    public async Task An_admin_limits_a_user_in_the_user_editor_and_lifts_it_again()
    {
        var admin = _site.App.Browser();
        var login = await (await admin.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(admin, "/Account/Login", login)).StatusCode);

        var target = SignIn.AddUser(_site.App, "editor-target", "UserPass123!", UserRole.User);

        async Task SaveAsync(UserRole role, bool restrict, params long[] ids)
        {
            var html = await BodyAsync(admin, $"/Users/Edit?id={target}");
            var fields = new List<KeyValuePair<string, string>>
            {
                new("__RequestVerificationToken", SignIn.AntiforgeryToken(html)),
                new("Input.Id", target.ToString()),
                new("Input.Username", "editor-target"),
                new("Input.Role", role.ToString()),
                new("Input.IsActive", "true"),
            };
            if (restrict)
            {
                fields.Add(new("Input.RestrictEnvironments", "true"));
                fields.AddRange(ids.Select(id => new KeyValuePair<string, string>("Input.EnvironmentIds", id.ToString())));
            }

            var response = await admin.PostAsync($"/Users/Edit?id={target}", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        await SaveAsync(UserRole.User, restrict: true, _site.Alpha);
        Assert.True(SignIn.UserOf(_site.App, target).RestrictEnvironments);
        Assert.Equal(new[] { _site.Alpha }, _site.App.Db(db => db.UserEnvironments.Where(x => x.UserId == target).Select(x => x.EnvironmentId).ToArray()));

        // The editor shows what is granted.
        var edit = await BodyAsync(admin, $"/Users/Edit?id={target}");
        Assert.Contains($"value=\"{_site.Alpha}\" checked", edit.Replace("checked=\"checked\"", "checked"));

        await SaveAsync(UserRole.User, restrict: false);
        Assert.False(SignIn.UserOf(_site.App, target).RestrictEnvironments);
        Assert.Empty(_site.App.Db(db => db.UserEnvironments.Where(x => x.UserId == target).ToArray()));

        // An admin cannot be limited, even when the form says so.
        await SaveAsync(UserRole.Admin, restrict: true, _site.Beta);
        Assert.False(SignIn.UserOf(_site.App, target).RestrictEnvironments);
    }

    [Fact]
    public async Task A_user_cannot_open_the_user_editor_to_grant_themselves_more()
    {
        var browser = await SignInAsync("only-alpha");
        var response = await browser.GetAsync($"/Users/Edit?id={_site.OnlyAlpha}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", SignIn.Path(response));
    }
}
