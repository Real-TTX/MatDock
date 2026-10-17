using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatDock.Core.Entities;
using MatDock.Core.Users;
using MatPlatform;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace MatDock.Tests.Platform;

/// <summary>Helpers of the sign-in tests.</summary>
internal static partial class SignIn
{
    public const string SessionCookie = "matdock.auth";

    /// <summary>Posts the local form of <paramref name="page"/> (which <paramref name="browser"/> has just loaded) to <paramref name="url"/>.</summary>
    public static Task<HttpResponseMessage> PostLocalFormAsync(HttpClient browser, string url, string page, string user = MatDockFactory.Admin, string password = MatDockFactory.AdminPassword)
        => browser.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(page),
            ["Input.Username"] = user,
            ["Input.Password"] = password,
        }));

    public static string AntiforgeryToken(string html) => TokenRegex().Match(html).Groups[1].Value;

    public static string Path(HttpResponseMessage response) => response.Headers.Location!.OriginalString.Replace("http://localhost", string.Empty);

    /// <summary>Adds an account with a password (like the user management).</summary>
    public static long AddUser(MatDockFactory site, string username, string password, UserRole role = UserRole.User)
    {
        using var scope = site.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<UserService>().CreateAsync(username, username, password, role).GetAwaiter().GetResult().Id;
    }

    public static User UserOf(MatDockFactory site, long id) => site.Db(db => db.Users.Single(u => u.Id == id));

    /// <summary>The person signs in at MatIdentity (the callback without a running MatIdentity); a browser that keeps what the callback set.</summary>
    public static async Task<(string Target, string SetCookie, HttpClient Browser)> MatIdentitySignInAsync(MatDockFactory site, MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = MatDockFactory.Context(scope);

        var target = await MatIdentitySignIn.CompleteSignInAsync(http, person, "/");
        var setCookie = http.Response.Headers.SetCookie.ToString();

        var cookies = new CookieContainer();
        foreach (var name in new[] { MatIdentityPendingLink.CookieName, SessionCookie })
        {
            var match = Regex.Match(setCookie, Regex.Escape(name) + "=([^;]+)");
            if (match.Success)
            {
                cookies.Add(new Uri("http://localhost"), new Cookie(name, match.Groups[1].Value, "/"));
            }
        }

        return (target, setCookie, site.Browser(cookies));
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex TokenRegex();
}

/// <summary>Without <c>MATIDENTITY_AUTHORITY</c> nothing changes: the local form, as before.</summary>
public sealed class LocalSignInTests(MatDockFactory site) : IClassFixture<MatDockFactory>
{
    [Fact]
    public async Task The_sign_in_page_is_the_local_form_and_it_works()
    {
        var browser = site.Browser();
        var unsigned = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, unsigned.StatusCode);
        Assert.StartsWith("/Account/Login", SignIn.Path(unsigned));

        var page = await browser.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("MatIdentity", html);

        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Account/Login", html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SignIn.SessionCookie + "=", StringComparison.Ordinal));
        var index = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);

        // The sign-out goes back to the local sign-in, not to MatIdentity.
        var signedOut = await browser.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(await index.Content.ReadAsStringAsync()),
        }));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/Account/Login", SignIn.Path(signedOut));
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var response = await SignIn.PostLocalFormAsync(browser, "/Account/Login", html, MatDockFactory.Admin, "wrong");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.StartsWith(SignIn.SessionCookie + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task There_is_no_matidentity_callback()
        => Assert.NotEqual(HttpStatusCode.OK, (await site.Browser().GetAsync("/signin-oidc")).StatusCode);
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c>: the sign-in page goes straight to MatIdentity, with the authorization code and PKCE.</summary>
public sealed class IdentityAutoSignInTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [Fact]
    public async Task The_sign_in_page_goes_straight_to_matidentity_with_code_and_pkce()
    {
        var response = await site.Browser().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/authorize", location.OriginalString);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("matdock", query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.Equal("http://localhost/signin-oidc", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task Behind_a_tls_proxy_the_redirect_address_is_https()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await site.Browser().SendAsync(request);

        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("https://localhost/signin-oidc", query["redirect_uri"].ToString());
    }

    [Theory]
    [InlineData("/Account/Login?local=1")]
    [InlineData("/Account/Login?local=true")]
    [InlineData("/Account/Login?signedOut=1")]
    [InlineData("/Account/Login?local=1&ssoError=1")]
    public async Task The_flags_show_the_page_instead_of_going_to_matidentity(string url)
    {
        var response = await site.Browser().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-sso-button\"", html);
        Assert.Equal(url.Contains("local", StringComparison.Ordinal), html.Contains("id=\"login-form\"", StringComparison.Ordinal));
        Assert.Equal(url.Contains("ssoError", StringComparison.Ordinal), html.Contains("id=\"login-sso-error\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_local_sign_in_still_works_with_local_1()
    {
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Account/Login?local=1")).Content.ReadAsStringAsync();
        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Account/Login?local=1", html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/", SignIn.Path(signedIn));
    }

    [Fact]
    public async Task The_definition_names_matidentity()
    {
        using var doc = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api"));
        Assert.Equal(IdentityFactory.Authority, doc.RootElement.GetProperty("auth").GetProperty("identity").GetString());
    }

    [Fact]
    public async Task A_new_person_gets_an_account_with_the_role_and_a_session_with_the_id_token()
    {
        var (target, setCookie, browser) = await SignIn.MatIdentitySignInAsync(site,
            new MatIdentityPerson("sub-new", "new@example.com", "New Person", "newperson", "admin", "id-token-new", new()));

        Assert.Equal("/", target);
        Assert.Contains(SignIn.SessionCookie + "=", setCookie);
        var user = site.Db(db => db.Users.Single(u => u.ExternalSubject == "sub-new"));
        Assert.Equal("newperson", user.Username);
        Assert.Equal("New Person", user.DisplayName);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal(string.Empty, user.PasswordHash);
        Assert.Equal("id-token-new", site.Db(db => db.UserSessions.Single(s => s.UserId == user.Id).IdToken));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);

        // The next sign-in finds the account by sub and follows the role.
        var (again, _, _) = await SignIn.MatIdentitySignInAsync(site,
            new MatIdentityPerson("sub-new", "new@example.com", "New Person", "newperson", "user", "id-token-2", new()));
        Assert.Equal("/", again);
        Assert.Equal(UserRole.User, SignIn.UserOf(site, user.Id).Role);
        Assert.Equal(1, site.Db(db => db.Users.Count(u => u.ExternalSubject == "sub-new")));
    }

    [Fact]
    public async Task Without_a_role_claim_the_local_role_stays()
    {
        await SignIn.MatIdentitySignInAsync(site, new MatIdentityPerson("sub-norole", null, "No Role", "norole", null, null, new()));
        var id = site.Db(db => db.Users.Single(u => u.ExternalSubject == "sub-norole").Id);
        site.Db(db =>
        {
            db.Users.Single(u => u.Id == id).Role = UserRole.Admin;
            return db.SaveChanges();
        });

        await SignIn.MatIdentitySignInAsync(site, new MatIdentityPerson("sub-norole", null, "No Role", "norole", null, null, new()));
        Assert.Equal(UserRole.Admin, SignIn.UserOf(site, id).Role);
    }

    [Fact]
    public async Task A_disabled_account_is_refused()
    {
        await SignIn.MatIdentitySignInAsync(site, new MatIdentityPerson("sub-off", null, "Off", "offperson", null, null, new()));
        site.Db(db =>
        {
            db.Users.Single(u => u.ExternalSubject == "sub-off").IsActive = false;
            return db.SaveChanges();
        });

        var (target, setCookie, _) = await SignIn.MatIdentitySignInAsync(site, new MatIdentityPerson("sub-off", null, "Off", "offperson", null, null, new()));
        Assert.Equal("/Account/AccessDenied", target);
        Assert.DoesNotContain(SignIn.SessionCookie + "=", setCookie);
    }

    [Fact]
    public async Task The_sign_out_ends_the_session_at_matidentity_too()
    {
        var (_, _, browser) = await SignIn.MatIdentitySignInAsync(site,
            new MatIdentityPerson("sub-out", null, "Out", "outperson", null, "id-token-out", new()));
        var index = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);

        var signedOut = await browser.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(await index.Content.ReadAsStringAsync()),
        }));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        var location = signedOut.Headers.Location!.OriginalString;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/logout", location);
        Assert.Equal("id-token-out", QueryHelpers.ParseQuery(new Uri(location).Query)["id_token_hint"].ToString());

        var user = site.Db(db => db.Users.Single(u => u.ExternalSubject == "sub-out"));
        Assert.False(site.Db(db => db.UserSessions.Any(s => s.UserId == user.Id)));
    }
}

/// <summary>The role of MatIdentity never demotes the last active administrator.</summary>
public sealed class LastAdminTests
{
    [Fact]
    public async Task The_last_administrator_keeps_the_role()
    {
        using var site = new IdentityFactory();
        await SignIn.MatIdentitySignInAsync(site, new MatIdentityPerson("sub-boss", null, "Boss", "boss", "admin", null, new()));
        var id = site.Db(db => db.Users.Single(u => u.ExternalSubject == "sub-boss").Id);
        site.Db(db =>
        {
            foreach (var other in db.Users.Where(u => u.Id != id))
            {
                other.IsActive = false;
            }

            return db.SaveChanges();
        });

        await SignIn.MatIdentitySignInAsync(site, new MatIdentityPerson("sub-boss", null, "Boss", "boss", "user", null, new()));
        Assert.Equal(UserRole.Admin, SignIn.UserOf(site, id).Role);
    }
}

/// <summary>MatIdentity is down: the sign-in page shows the local form with a message instead of failing.</summary>
public sealed class UnreachableIdentityTests(UnreachableIdentityFactory site) : IClassFixture<UnreachableIdentityFactory>
{
    [Fact]
    public async Task The_local_form_is_the_way_in()
    {
        var response = await site.Browser().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"login-sso-error\"", html);
    }
}
