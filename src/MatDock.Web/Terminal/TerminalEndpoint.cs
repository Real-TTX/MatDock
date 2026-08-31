using System.Net.WebSockets;
using System.Text.Json;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Ssh;
using MatDock.Core.Volumes;
using Renci.SshNet;

namespace MatDock.Web.Terminal;

/// <summary>
/// WebSocket ⇄ SSH PTY bridge for the web terminal. Admin-only (enforced by the endpoint's authorization).
/// Opens a login shell on the environment host, or — when a container id is given — <c>docker exec -it</c>
/// into that container. Binary frames carry raw terminal bytes both ways; text frames are control messages
/// (window resize). The browser side runs xterm.js.
/// </summary>
public static class TerminalEndpoint
{
    private const int BufferSize = 8 * 1024;

    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Defence-in-depth against Cross-Site WebSocket Hijacking: WS handshakes bypass CORS, so on top of
        // the SameSite=Lax auth cookie, require a same-origin Origin header (when the browser sends one).
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            !(Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
              string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Terminal");

        // ----- parse + validate query BEFORE accepting the socket -----
        if (!long.TryParse(context.Request.Query["envId"], out var envId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var container = context.Request.Query["container"].ToString();
        if (!string.IsNullOrEmpty(container) && !ContainerCommands.IsValidId(container))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var cols = ParseDim(context.Request.Query["cols"], 80, 500);
        var rows = ParseDim(context.Request.Query["rows"], 24, 200);

        var environmentService = context.RequestServices.GetRequiredService<EnvironmentService>();
        var sshFactory = context.RequestServices.GetRequiredService<ISshClientFactory>();

        var env = await environmentService.GetAsync(envId, context.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var settings = environmentService.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);

        // The interactive terminal still uses an SSH PTY; a local PTY is a separate follow-up. Give local
        // environments a clear message instead of a confusing "SSH to local" failure.
        if (settings.IsLocal)
        {
            using var localWs = await context.WebSockets.AcceptWebSocketAsync();
            await TrySendTextAsync(localWs, "\r\n\x1b[33m[Terminal für lokale Umgebungen wird noch nicht unterstützt.]\x1b[0m\r\n");
            try { await localWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "unsupported", CancellationToken.None); } catch { }
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var ct = cts.Token;

        SshClient? client = null;
        ShellStream? shell = null;
        try
        {
            client = sshFactory.Create(settings);
            // Detect a dead remote host so a leaked session doesn't pin the SSH connection forever.
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            await client.ConnectAsync(ct);

            shell = client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, BufferSize);

            if (!string.IsNullOrEmpty(container))
            {
                // Replace the login shell with an interactive exec into the container, so exiting the
                // container shell ends the SSH shell and closes the socket. Container id is validated.
                shell.Write("exec " + head + " exec -it " + VolumeFileCommands.ShellQuote(container) + " sh\n");
            }

            var pumpOut = PumpShellToSocketAsync(shell, ws, logger, ct);
            var pumpIn = PumpSocketToShellAsync(ws, shell, logger, ct);
            await Task.WhenAny(pumpOut, pumpIn);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Terminal session for env {EnvId} ended with an error.", envId);
            await TrySendTextAsync(ws, "\r\n\x1b[31m[Verbindung fehlgeschlagen: " + Sanitize(ex.Message) + "]\x1b[0m\r\n");
        }
        finally
        {
            cts.Cancel();
            shell?.Dispose();
            if (client is not null)
            {
                try { client.Disconnect(); } catch { /* ignore */ }
                client.Dispose();
            }

            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { /* ignore */ }
            }
        }
    }

    private static async Task PumpShellToSocketAsync(ShellStream shell, WebSocket ws, ILogger logger, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // ShellStream has no true async read; this runs the blocking read on a pool thread and is
                // unblocked by shell.Dispose() (returns 0) in the finally, which ends this loop.
                var read = await shell.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                {
                    break; // remote shell closed
                }

                await ws.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Terminal shell→socket pump ended with an error.");
        }
    }

    private static async Task PumpSocketToShellAsync(WebSocket ws, ShellStream shell, ILogger logger, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.Count <= 0)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    TryResize(shell, buffer, result.Count);
                    continue;
                }

                await shell.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                await shell.FlushAsync(ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Terminal socket→shell pump ended with an error.");
        }
    }

    /// <summary>Applies a <c>{"cols":C,"rows":R}</c> control frame to the remote PTY. Malformed frames are ignored.</summary>
    private static void TryResize(ShellStream shell, byte[] buffer, int count)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, count));
            var root = doc.RootElement;
            if (root.TryGetProperty("cols", out var c) && root.TryGetProperty("rows", out var r))
            {
                var cols = Math.Clamp(c.GetInt32(), 1, 500);
                var rows = Math.Clamp(r.GetInt32(), 1, 200);
                shell.ChangeWindowSize((uint)cols, (uint)rows, 0, 0);
            }
        }
        catch
        {
            // ignore malformed control frames / unsupported resize
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

    private static int ParseDim(string? raw, int fallback, int max)
        => int.TryParse(raw, out var v) && v > 0 ? Math.Min(v, max) : fallback;

    // Strip all C0 control chars (incl. ESC) so a remote error message can't inject terminal escapes.
    private static string Sanitize(string message)
        => new string(message.Where(ch => ch >= ' ').ToArray());
}
