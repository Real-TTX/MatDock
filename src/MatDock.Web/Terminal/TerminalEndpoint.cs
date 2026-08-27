using System.Net.WebSockets;
using MatDock.Core.Containers;
using MatDock.Core.Environments;
using MatDock.Core.Ssh;
using MatDock.Core.Volumes;
using Renci.SshNet;

namespace MatDock.Web.Terminal;

/// <summary>
/// WebSocket ⇄ SSH PTY bridge for the web terminal. Admin-only (enforced by the endpoint's authorization).
/// Opens a login shell on the environment host, or — when a container id is given — <c>docker exec -it</c>
/// into that container. Bytes are pumped verbatim in both directions; the browser side runs xterm.js.
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

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var ct = cts.Token;

        SshClient? client = null;
        ShellStream? shell = null;
        try
        {
            client = sshFactory.Create(settings);
            await client.ConnectAsync(ct);

            shell = client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, BufferSize);

            if (!string.IsNullOrEmpty(container))
            {
                // Replace the login shell with an interactive exec into the container, so exiting the
                // container shell ends the SSH shell and closes the socket. Container id is validated.
                shell.Write("exec " + head + " exec -it " + VolumeFileCommands.ShellQuote(container) + " sh\n");
            }

            var pumpOut = PumpShellToSocketAsync(shell, ws, ct);
            var pumpIn = PumpSocketToShellAsync(ws, shell, ct);
            await Task.WhenAny(pumpOut, pumpIn);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Terminal session for env {EnvId} ended with an error.", envId);
            await TrySendTextAsync(ws, "\r\n[31m[Verbindung fehlgeschlagen: " + Sanitize(ex.Message) + "][0m\r\n", CancellationToken.None);
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

    private static async Task PumpShellToSocketAsync(ShellStream shell, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await shell.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (read <= 0)
            {
                break; // remote shell closed
            }

            await ws.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
    }

    private static async Task PumpSocketToShellAsync(WebSocket ws, ShellStream shell, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (!ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.Count > 0)
            {
                await shell.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                await shell.FlushAsync(ct);
            }
        }
    }

    private static async Task TrySendTextAsync(WebSocket ws, string text, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        catch { /* best effort */ }
    }

    private static int ParseDim(string? raw, int fallback, int max)
        => int.TryParse(raw, out var v) && v > 0 ? Math.Min(v, max) : fallback;

    private static string Sanitize(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}
