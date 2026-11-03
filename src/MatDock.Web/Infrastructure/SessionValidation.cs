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
        MatDock.Core.Entities.UserSession? session;
        try
        {
            session = await sessions.ValidateAsync(token, CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException or TimeoutException)
        {
            // The database cannot answer right now (busy, a disk under load): the cookie is valid and not expired, so this request keeps it. Signing the user
            // out here would end sessions whenever the server is busy; a revoked session is refused at the next request that reaches the database.
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SessionValidation))
                .LogWarning(ex, "The session could not be checked against the database; the request keeps its cookie.");
            return;
        }

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
