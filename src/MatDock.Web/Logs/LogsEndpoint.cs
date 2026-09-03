using System.Net.WebSockets;
using MatDock.Core.Containers;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Volumes;

namespace MatDock.Web.Logs;

/// <summary>
/// Streams a container's live logs (<c>docker logs -f</c>) over a one-directional WebSocket. Works for SSH
/// and local environments (the log output is read from the command's stdout stream). Authenticated users
/// only (same access level as the static Logs page); the handler validates env + container id.
/// </summary>
public static class LogsEndpoint
{
    private const int BufferSize = 16 * 1024;

    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Defence-in-depth against Cross-Site WebSocket Hijacking (WS handshakes bypass CORS): require a
        // same-origin Origin header on top of the SameSite=Lax auth cookie.
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            !(Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
              string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Logs");

        // ----- validate query BEFORE accepting the socket -----
        if (!long.TryParse(context.Request.Query["envId"], out var envId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var container = context.Request.Query["container"].ToString();
        if (string.IsNullOrEmpty(container) || !ContainerCommands.IsValidId(container))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var tail = ParseTail(context.Request.Query["tail"]);

        var environmentService = context.RequestServices.GetRequiredService<EnvironmentService>();
        var sessionFactory = context.RequestServices.GetRequiredService<IHostSessionFactory>();

        var env = await environmentService.GetAsync(envId, context.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var settings = environmentService.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var ct = cts.Token;

        IHostSession? session = null;
        IHostCommand? cmd = null;
        try
        {
            session = sessionFactory.Create(settings);
            await session.ConnectAsync(ct);

            cmd = session.CreateCommand(ContainerCommands.LogsFollow(head, container, tail));
            cmd.CommandTimeout = Timeout.InfiniteTimeSpan; // follow runs until the client disconnects
            cmd.BufferOutput = false;
            var async = cmd.BeginExecute();

            var pumpOut = PumpLogsToSocketAsync(cmd.OutputStream, ws, logger, ct);
            var pumpIn = DrainSocketAsync(ws, ct);
            await Task.WhenAny(pumpOut, pumpIn);

            try { cmd.EndExecute(async); } catch { /* stream ended / cancelled */ }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Log stream for env {EnvId} container {Container} ended with an error.", envId, container);
            await TrySendTextAsync(ws, "\r\n[Log stream failed: " + Sanitize(ex.Message) + "]\r\n");
        }
        finally
        {
            cts.Cancel();
            cmd?.Dispose();
            session?.Dispose();

            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { /* ignore */ }
            }
        }
    }

    private static async Task PumpLogsToSocketAsync(Stream output, WebSocket ws, ILogger logger, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                {
                    break; // log command ended (container stopped / removed)
                }

                await ws.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Log→socket pump ended with an error.");
        }
    }

    /// <summary>Reads (and ignores) client frames so a client-initiated close ends the stream promptly.</summary>
    private static async Task DrainSocketAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // client vanished; let the caller tear down.
        }
    }

    private static async Task TrySendTextAsync(WebSocket ws, string text)
    {
        if (ws.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        }
        catch { /* best effort */ }
    }

    private static int ParseTail(string? raw)
        => int.TryParse(raw, out var v) && v >= 0 ? Math.Min(v, 5000) : 200;

    // Strip control chars so a remote error message can't inject anything odd into the log pane.
    private static string Sanitize(string message)
        => new string(message.Where(ch => ch >= ' ').ToArray());
}
