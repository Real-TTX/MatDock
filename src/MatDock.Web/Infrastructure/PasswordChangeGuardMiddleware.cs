namespace MatDock.Web.Infrastructure;

/// <summary>
/// While a user is flagged <c>MustChangePassword</c> (e.g. the seeded admin), this middleware forces
/// every request to the profile page until a new password is set. Account and static routes stay open.
/// </summary>
public sealed class PasswordChangeGuardMiddleware
{
    private const string ChangePasswordPath = "/Account/Profile";
    private readonly RequestDelegate _next;

    public PasswordChangeGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true && user.MustChangePassword())
        {
            var path = context.Request.Path;
            if (!path.StartsWithSegments("/Account") &&
                !path.StartsWithSegments("/css") &&
                !path.StartsWithSegments("/js") &&
                !path.StartsWithSegments("/img") &&
                !path.StartsWithSegments("/lib"))
            {
                context.Response.Redirect(ChangePasswordPath);
                return;
            }
        }

        await _next(context);
    }
}
