using System.Net;
using MatPlatform;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MatDock.Tests.Platform;

/// <summary>
/// The back-channel logout of MatIdentity (MatPlatform 1.4.0, <c>POST /signout-backchannel</c>): a signed logout token ends the MatDock sessions it
/// names (the <c>sid</c> kept with the session at the sign-in, or every session of the linked user for <c>sub</c> alone); their next request is
/// signed out. Each test starts its own app.
/// </summary>
public sealed class BackchannelLogoutTests
{
    private const string Path = "/signout-backchannel";

    [Fact]
    public async Task A_valid_token_ends_the_session_it_names_and_its_next_request_is_signed_out()
    {
        using var site = new IdentityFactory();
        var (_, _, first) = await SignIn.MatIdentitySignInAsync(site, Person("sub-anna", "sid-1"));
        var (_, _, second) = await SignIn.MatIdentitySignInAsync(site, Person("sub-anna", "sid-2"));
        Assert.Equal("sid-1", site.Db(db => db.UserSessions.Where(s => s.User!.ExternalSubject == "sub-anna").OrderBy(s => s.Id).First().ExternalSessionId));
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/")).StatusCode);

        var response = await Post(site, Token("sub-anna", "sid-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        await AssertSignedOutAsync(first);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/")).StatusCode);

        var entry = site.Db(db => db.ActivityEntries.Single(e => e.Actor == "MatIdentity"));
        Assert.Equal("anna", entry.Target);
        Assert.Equal("api", entry.Source);
        Assert.True(entry.Ok);
    }

    [Fact]
    public async Task The_subject_alone_ends_every_session_of_the_linked_user()
    {
        using var site = new IdentityFactory();
        var (_, _, first) = await SignIn.MatIdentitySignInAsync(site, Person("sub-anna", "sid-1"));
        var (_, _, second) = await SignIn.MatIdentitySignInAsync(site, Person("sub-anna", "sid-2"));
        var (_, _, other) = await SignIn.MatIdentitySignInAsync(site, Person("sub-bert", "sid-3"));

        Assert.Equal(HttpStatusCode.OK, (await Post(site, Token("sub-anna", null))).StatusCode);

        await AssertSignedOutAsync(first);
        await AssertSignedOutAsync(second);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_subject_or_session_ends_nothing_and_is_no_error()
    {
        using var site = new IdentityFactory();
        var (_, _, browser) = await SignIn.MatIdentitySignInAsync(site, Person("sub-anna", "sid-1"));

        Assert.Equal(HttpStatusCode.OK, (await Post(site, Token("sub-nobody", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(site, Token(null, "sid-unknown"))).StatusCode);
        // A sid of another person's subject ends nothing either.
        Assert.Equal(HttpStatusCode.OK, (await Post(site, Token("sub-bert", "sid-1"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
        Assert.False(site.Db(db => db.ActivityEntries.Any(e => e.Actor == "MatIdentity")));
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_400_and_ends_nothing()
    {
        using var site = new IdentityFactory();
        var (_, _, browser) = await SignIn.MatIdentitySignInAsync(site, Person("sub-anna", "sid-1"));

        var response = await Post(site, Token("sub-anna", "sid-1", MatDockFactory.NewCredentials("forged")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Without_matidentity_there_is_no_endpoint()
    {
        using var site = new MatDockFactory();

        // Not mapped: for a signed-in browser it is 404; anonymously it is like every unknown path of MatDock (the fallback policy
        // sends it to the sign-in page), not the endpoint.
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Account/Login", html)).StatusCode);
        var token = new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = Token("sub-anna", "sid-1") });
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync(Path, token)).StatusCode);

        var anonymous = await Post(site, Token("sub-anna", "sid-1"));
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/Account/Login", SignIn.Path(anonymous));
    }

    private static MatIdentityPerson Person(string subject, string sessionId)
    {
        var name = subject["sub-".Length..];
        return new MatIdentityPerson(subject, name + "@example.com", name, name, "user", "id-token-" + sessionId, new()) { SessionId = sessionId };
    }

    private static async Task AssertSignedOutAsync(HttpClient browser)
    {
        var response = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", SignIn.Path(response));
    }

    private static Task<HttpResponseMessage> Post(MatDockFactory site, string token)
        => site.Browser().PostAsync(Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));

    /// <summary>A logout token as MatIdentity writes it.</summary>
    private static string Token(string? subject, string? sessionId, SigningCredentials? credentials = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = IdentityFactory.Authority,
            ["aud"] = "matdock",
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 120,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["events"] = new Dictionary<string, object> { [MatIdentityLogoutToken.EventType] = new Dictionary<string, object>() },
        };
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (sessionId is not null)
        {
            claims["sid"] = sessionId;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            TokenType = MatIdentityLogoutToken.TokenType,
            SigningCredentials = credentials ?? MatDockFactory.SigningCredentials,
        });
    }
}
