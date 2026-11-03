using MatDock.Core.Notifications.WebPush;
using MatDock.Web.Infrastructure;

namespace MatDock.Web.Support;

/// <summary>
/// The browser side of Web Push (pwa.js): the public key of this installation, and the device of the signed-in user switched on, off or tried. The posts
/// need the header <c>X-MatDock-Request</c>, which another site cannot send without asking first (CORS), so no page elsewhere can switch a device.
/// </summary>
public static class PushEndpoints
{
    public const string Root = "/ui/push";
    public const string Header = "X-MatDock-Request";

    public static void MapPushEndpoints(this WebApplication app)
    {
        var push = app.MapGroup(Root).RequireAuthorization();
        push.MapGet("/key", (WebPushKeys keys) => Results.Json(new { publicKey = keys.PublicKey }));
        push.MapPost("/devices", async (BrowserPushSubscription body, HttpContext http, WebPushService devices) =>
        {
            if (Refused(http) is { } refused)
            {
                return refused;
            }

            var problem = await devices.RegisterAsync(http.User.GetUserId()!.Value, body, http.Request.Headers.UserAgent.ToString(), http.RequestAborted);
            return problem is null ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = problem });
        });
        push.MapPost("/devices/remove", async (RemoveBody body, HttpContext http, WebPushService devices) =>
        {
            if (Refused(http) is { } refused)
            {
                return refused;
            }

            var removed = await devices.RemoveAsync(http.User.GetUserId()!.Value, body.Endpoint, body.Id, http.RequestAborted);
            return Results.Ok(new { ok = removed });
        });
        push.MapPost("/test", async (HttpContext http, WebPushService devices) =>
        {
            if (Refused(http) is { } refused)
            {
                return refused;
            }

            var mine = await devices.OfUserAsync(http.User.GetUserId()!.Value, http.RequestAborted);
            var reached = 0;
            foreach (var device in mine)
            {
                if (await devices.SendAsync(device, "MatDock", "Notifications reach this device.", "/", http.RequestAborted) == PushOutcome.Delivered)
                {
                    reached++;
                }
            }

            return Results.Ok(new { devices = mine.Count, reached });
        });
    }

    public sealed record RemoveBody(string? Endpoint, long? Id);

    private static IResult? Refused(HttpContext http)
        => http.Request.Headers[Header] != "1" ? Results.BadRequest(new { error = "Missing header." })
         : http.User.GetUserId() is null ? Results.Unauthorized()
         : null;
}
