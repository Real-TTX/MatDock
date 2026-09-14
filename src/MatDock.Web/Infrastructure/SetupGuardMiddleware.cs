using MatDock.Core.Users;

namespace MatDock.Web.Infrastructure;

/// <summary>Tracks whether first-run setup is complete, so the guard skips the DB check on every
/// request once at least one user exists. Registered as a singleton.</summary>
public sealed class SetupState
{
    public volatile bool Completed;
}

/// <summary>
/// On a fresh install (no users yet) every request is redirected to the first-run setup wizard
/// (<c>/Setup</c>), which creates the first administrator. Once a user exists this becomes a no-op.
/// </summary>
public sealed class SetupGuardMiddleware
{
    private const string SetupPath = "/Setup";
    private readonly RequestDelegate _next;
    private readonly SetupState _state;

    public SetupGuardMiddleware(RequestDelegate next, SetupState state)
    {
        _next = next;
        _state = state;
    }

    public async Task InvokeAsync(HttpContext context, UserService users)
    {
        if (!_state.Completed)
        {
            if (await users.CountAsync(context.RequestAborted) > 0)
            {
                _state.Completed = true;
            }
            else
            {
                var path = context.Request.Path;
                if (!path.StartsWithSegments(SetupPath) &&
                    !path.StartsWithSegments("/css") &&
                    !path.StartsWithSegments("/js") &&
                    !path.StartsWithSegments("/img") &&
                    !path.StartsWithSegments("/lib"))
                {
                    context.Response.Redirect(SetupPath);
                    return;
                }
            }
        }

        await _next(context);
    }
}
