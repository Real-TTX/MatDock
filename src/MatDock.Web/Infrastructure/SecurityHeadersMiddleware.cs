namespace MatDock.Web.Infrastructure;

/// <summary>
/// The headers that keep the browser on MatDock's side, on every answer: no framing (a page that controls Docker hosts must not be clicked through
/// from another site), no type sniffing, no referrer to other sites, a content security policy that allows only MatDock's own scripts, styles,
/// connections and WebSockets (images may come from elsewhere: the icons of the apps), and no camera, microphone or position.
/// HSTS is added by <c>UseHsts</c> over HTTPS outside development.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    /// <summary>The content security policy for a request to <paramref name="host"/> (the WebSockets of the logs and the terminal go to the same host).</summary>
    public static string Policy(string host)
        => "default-src 'self'; "
           + "script-src 'self'; "
           // xterm.js and the toolkit set styles from script; the markup itself has none (UiTests)
           + "style-src 'self' 'unsafe-inline'; "
           + "img-src 'self' data: blob: https: http:; "
           + "font-src 'self' data:; "
           + $"connect-src 'self' ws://{host} wss://{host}; "
           + "worker-src 'self'; "
           + "manifest-src 'self'; "
           + "object-src 'none'; "
           + "base-uri 'self'; "
           + "frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers.ContentSecurityPolicy = Policy(context.Request.Host.Value ?? "localhost");
            return Task.CompletedTask;
        });

        return _next(context);
    }
}
