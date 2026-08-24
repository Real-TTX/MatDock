using MatDock.Core.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Cookie <c>OnValidatePrincipal</c> handler: on every request it re-checks the session token against
/// SQLite, so a session can be revoked server-side (deleted user, expired or invalidated session) and
/// so sessions keep working across container restarts (the token lives in the database on the volume).
/// </summary>
public static class SessionValidation
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var token = principal?.GetSessionToken();
        if (principal is null || string.IsNullOrEmpty(token))
        {
            await RejectAsync(context);
            return;
        }

        var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
        var session = await sessions.ValidateAsync(token, context.HttpContext.RequestAborted);
        if (session?.User is null)
        {
            await RejectAsync(context);
            return;
        }

        // Re-sync claims from the database so a role change or a forced password change takes effect
        // on the next request instead of only after the cookie expires.
        var user = session.User;
        if (principal.GetRole() != user.Role || principal.MustChangePassword() != user.MustChangePassword)
        {
            context.ReplacePrincipal(PrincipalFactory.Build(user, token));
            context.ShouldRenew = true;
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AuthConstants.CookieScheme);
    }
}
