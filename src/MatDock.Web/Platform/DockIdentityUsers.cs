using System.Globalization;
using MatDock.Core.Activity;
using MatDock.Core.Auth;
using MatDock.Core.Entities;
using MatDock.Core.Users;
using MatDock.Web.Infrastructure;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;

namespace MatDock.Web.Platform;

/// <summary>
/// MatDock's side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): the person is mapped to a local user by
/// <see cref="UserService.SignInExternalAsync"/> (by <c>sub</c>, else a new user; the claim <c>role</c> when it is there) and then signed in with
/// MatDock's own server-side session and cookie, like a local sign-in, keeping the ID token and the <c>sid</c> with the session for the sign-out at
/// MatIdentity and its back-channel logout. An account with the same user name is not linked by the name: the answer is
/// <see cref="MatIdentitySignInResult.LinkRequired"/>, and the sign-in page links it after its password (<see cref="MatIdentityPendingLink"/>).
/// The back-channel logout (<see cref="IMatIdentitySessions"/>) deletes the sessions it names (<see cref="SessionService.EndExternalAsync"/>);
/// their next request is signed out (<see cref="SessionValidation"/>).
/// </summary>
public sealed class DockIdentityUsers(UserService users, SessionService sessions, ActivityLogService activity) : IMatIdentityUsers, IMatIdentitySessions
{
    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var result = await users.SignInExternalAsync(ToIdentity(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role), ct);
        if (result.Refused)
        {
            return MatIdentitySignInResult.Refused(result.Error!);
        }

        if (result.LinkUserId is { } linkUserId)
        {
            return MatIdentitySignInResult.LinkRequired(linkUserId.ToString(CultureInfo.InvariantCulture));
        }

        await SignInLocallyAsync(http, sessions, result.User!, persistent: true, person.IdToken, ct, person.SessionId);
        return MatIdentitySignInResult.Success();
    }

    public async Task EndSessionsAsync(string? subject, string? sessionId, CancellationToken ct)
    {
        foreach (var (username, count) in await sessions.EndExternalAsync(subject, sessionId, ct))
        {
            await activity.RecordAsync(
                "MatIdentity",
                "api",
                "Sign-out at MatIdentity",
                username,
                null,
                true,
                string.Create(CultureInfo.InvariantCulture, $"{count} session(s) ended (back-channel logout{(sessionId is null ? ", every session" : string.Empty)})"),
                ct);
        }
    }

    public static ExternalIdentity ToIdentity(string subject, string? email, string? name, string? preferredUserName, string? role)
        => new(subject, email, name, preferredUserName, role);

    /// <summary>
    /// Opens a MatDock session for <paramref name="user"/> and sets the cookie (the same for a local sign-in and one through MatIdentity);
    /// <paramref name="sessionId"/> is the <c>sid</c> of MatIdentity when it came through it.
    /// </summary>
    public static async Task SignInLocallyAsync(HttpContext http, SessionService sessions, User user, bool persistent, string? idToken, CancellationToken ct,
        string? sessionId = null)
    {
        var session = await sessions.CreateAsync(
            user.Id,
            http.Request.Headers.UserAgent.ToString(),
            http.Connection.RemoteIpAddress?.ToString(),
            ct,
            idToken,
            sessionId);

        await http.SignInAsync(
            AuthConstants.CookieScheme,
            PrincipalFactory.Build(user, session.Token),
            new AuthenticationProperties { IsPersistent = persistent });
    }
}
