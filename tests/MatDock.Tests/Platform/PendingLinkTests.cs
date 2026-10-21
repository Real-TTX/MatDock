using System.Net;
using System.Text.Json;
using MatDock.Core.Entities;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace MatDock.Tests.Platform;

/// <summary>
/// An account whose user name matches a person of MatIdentity is linked only after its password: anyone may register any name, otherwise someone
/// could take the name of a victim beforehand and receive the victim in their account at the first sign-in through MatIdentity.
/// Each test starts its own app.
/// </summary>
public sealed class PendingLinkTests
{
    private const string ClaimedName = "victim";
    private const string ClaimedPassword = "claimer-knows-this";

    [Fact]
    public async Task A_pre_claimed_name_is_not_entered_and_the_browser_lands_on_the_local_sign_in()
    {
        using var site = Site(out var claimedId);
        var users = site.Db(db => db.Users.Count());

        var (target, setCookie, browser) = await SignIn.MatIdentitySignInAsync(site, Person());

        Assert.Equal("/Account/Login?local=1&link=1", target);
        Assert.DoesNotContain(SignIn.SessionCookie + "=", setCookie);
        Assert.Contains(MatIdentityPendingLink.CookieName + "=", setCookie);
        Assert.Null(SignIn.UserOf(site, claimedId).ExternalSubject);
        Assert.Equal(users, site.Db(db => db.Users.Count()));
        Assert.False(site.Db(db => db.UserSessions.Any(s => s.UserId == claimedId)));

        var page = await browser.GetAsync(target);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-link\"", html);
        Assert.Contains("id=\"lc-cancel\"", html);
        Assert.Contains("id=\"login-form\"", html);
    }

    [Fact]
    public async Task The_local_sign_in_of_the_matched_account_links_it_with_the_role_and_the_id_token()
    {
        using var site = Site(out var claimedId);
        var (target, _, browser) = await SignIn.MatIdentitySignInAsync(site, Person());
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        // A wrong password links nothing and leaves the pending link in place.
        var wrong = await SignIn.PostLocalFormAsync(browser, target, html, ClaimedName, "wrong");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        html = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-link\"", html);
        Assert.Null(SignIn.UserOf(site, claimedId).ExternalSubject);

        var signedIn = await SignIn.PostLocalFormAsync(browser, target, html, ClaimedName, ClaimedPassword);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        var linked = SignIn.UserOf(site, claimedId);
        Assert.Equal("sub-victim", linked.ExternalSubject);
        Assert.Equal(UserRole.Admin, linked.Role);
        Assert.Equal("id-token-victim", site.Db(db => db.UserSessions.Single(s => s.UserId == claimedId).IdToken));
        Assert.Equal("sid-victim", site.Db(db => db.UserSessions.Single(s => s.UserId == claimedId).ExternalSessionId));

        // From now on MatIdentity finds the account by its subject and signs in directly.
        var (next, cookie, _) = await SignIn.MatIdentitySignInAsync(site, Person());
        Assert.Equal("/", next);
        Assert.Contains(SignIn.SessionCookie + "=", cookie);
    }

    [Fact]
    public async Task Another_account_signing_in_links_nothing_and_drops_the_pending_link()
    {
        using var site = Site(out var claimedId);
        var (target, _, browser) = await SignIn.MatIdentitySignInAsync(site, Person());
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        var signedIn = await SignIn.PostLocalFormAsync(browser, target, html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        Assert.Null(SignIn.UserOf(site, claimedId).ExternalSubject);
        Assert.False(site.Db(db => db.Users.Any(u => u.ExternalSubject == "sub-victim")));
        var admin = site.Db(db => db.Users.Single(u => u.Username == MatDockFactory.Admin));
        Assert.Null(admin.ExternalSubject);
        Assert.Null(site.Db(db => db.UserSessions.Single(s => s.UserId == admin.Id).IdToken));
    }

    [Fact]
    public async Task Cancel_deletes_the_pending_link_and_a_later_sign_in_links_nothing()
    {
        using var site = Site(out var claimedId);
        var (target, _, browser) = await SignIn.MatIdentitySignInAsync(site, Person());
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        var cancelled = await browser.PostAsync("/Account/Login?handler=CancelLink", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
        }));
        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        Assert.Equal("/Account/Login?local=1", SignIn.Path(cancelled));
        Assert.Contains(cancelled.Headers.GetValues("Set-Cookie"), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"login-link\"", html);
        Assert.Contains("id=\"login-link-expired\"", html);

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, target, html, ClaimedName, ClaimedPassword)).StatusCode);
        Assert.Null(SignIn.UserOf(site, claimedId).ExternalSubject);
    }

    [Fact]
    public async Task The_email_as_user_name_needs_the_proof_as_well()
    {
        using var site = new IdentityFactory();
        var claimedId = SignIn.AddUser(site, "victim@example.com", ClaimedPassword);

        var (target, setCookie, _) = await SignIn.MatIdentitySignInAsync(site,
            new MatIdentityPerson("sub-mail", "Victim@Example.com", "Victim", null, null, null, new()));

        Assert.Equal("/Account/Login?local=1&link=1", target);
        Assert.DoesNotContain(SignIn.SessionCookie + "=", setCookie);
        Assert.Null(SignIn.UserOf(site, claimedId).ExternalSubject);
    }

    [Fact]
    public async Task Several_matches_or_a_match_linked_elsewhere_are_refused()
    {
        using var site = new IdentityFactory();
        SignIn.AddUser(site, "alice", ClaimedPassword);
        SignIn.AddUser(site, "alice@example.com", ClaimedPassword);
        var users = site.Db(db => db.Users.Count());

        var (several, _, _) = await SignIn.MatIdentitySignInAsync(site, new MatIdentityPerson("sub-alice", "alice@example.com", "Alice", "alice", null, null, new()));
        Assert.Equal("/Account/AccessDenied", several);

        var bobId = SignIn.AddUser(site, "bob", ClaimedPassword);
        site.Db(db =>
        {
            db.Users.Single(u => u.Id == bobId).ExternalSubject = "sub-bob-original";
            return db.SaveChanges();
        });
        var (elsewhere, _, _) = await SignIn.MatIdentitySignInAsync(site, new MatIdentityPerson("sub-bob-other", null, "Bob", "bob", null, null, new()));
        Assert.Equal("/Account/AccessDenied", elsewhere);

        Assert.Equal(users + 1, site.Db(db => db.Users.Count()));
        Assert.Equal("sub-bob-original", SignIn.UserOf(site, bobId).ExternalSubject);
    }

    [Fact]
    public async Task A_forged_pending_cookie_is_ignored()
    {
        using var site = Site(out var claimedId);
        var json = JsonSerializer.Serialize(new MatIdentityPendingLink("sub-forged", null, null, ClaimedName, "admin", null, claimedId.ToString()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await AssertIgnoredAsync(site, claimedId, Uri.EscapeDataString(json));
    }

    [Fact]
    public async Task An_expired_pending_cookie_is_ignored()
    {
        using var site = Site(out var claimedId);
        var json = JsonSerializer.Serialize(new MatIdentityPendingLink("sub-expired", null, null, ClaimedName, "admin", null, claimedId.ToString()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var value = site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(MatIdentityPendingLink.Purpose).ToTimeLimitedDataProtector()
            .Protect(json, DateTimeOffset.UtcNow.AddSeconds(-1));
        await AssertIgnoredAsync(site, claimedId, value);
    }

    /// <summary>The app with the account that carries the victim's name; whoever created it knows its password.</summary>
    private static IdentityFactory Site(out long claimedId)
    {
        var site = new IdentityFactory();
        claimedId = SignIn.AddUser(site, ClaimedName, ClaimedPassword);
        return site;
    }

    private static MatIdentityPerson Person() => new("sub-victim", "victim@example.com", "Victim", ClaimedName, "admin", "id-token-victim", new()) { SessionId = "sid-victim" };

    private static async Task AssertIgnoredAsync(IdentityFactory site, long claimedId, string cookieValue)
    {
        var cookies = new CookieContainer();
        cookies.Add(new Uri("http://localhost"), new Cookie(MatIdentityPendingLink.CookieName, cookieValue, "/"));
        var browser = site.Browser(cookies);

        var html = await (await browser.GetAsync("/Account/Login?local=1&link=1")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("id=\"login-link\"", html);

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login?local=1&link=1", html, ClaimedName, ClaimedPassword)).StatusCode);
        Assert.Null(SignIn.UserOf(site, claimedId).ExternalSubject);
        Assert.False(site.Db(db => db.Users.Any(u => u.ExternalSubject == "sub-forged" || u.ExternalSubject == "sub-expired")));
    }
}
