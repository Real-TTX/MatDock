using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MatDock.Core;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using Microsoft.AspNetCore.Http.Features;

namespace MatDock.Web.Api;

/// <summary>
/// The bridge (<see cref="BridgeProtocol"/>): another MatDock runs its Docker commands on the environments of this one, as it would over SSH. Only a key
/// of the scope <see cref="ApiKeyScope.Bridge"/> opens it (<see cref="ApiKeys.AuthenticateAsync"/>), and a chain longer than
/// <see cref="BridgeProtocol.MaxHops"/> is refused (and with it a loop of bridges).
/// </summary>
public static class BridgeApi
{
    public static void MapBridgeApi(this WebApplication app)
    {
        var bridge = app.MapGroup(MatDockApi.BasePath + "/bridge")
            .AllowAnonymous() // the bridge key is the guard, not the session cookie
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var (_, denied) = await ApiKeys.AuthenticateAsync(http, bridge: true);
                if (denied is not null)
                {
                    return denied;
                }

                var hops = int.TryParse(http.Request.Headers[BridgeProtocol.HopsHeader], NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 1;
                if (hops > BridgeProtocol.MaxHops)
                {
                    return ApiResults.Error(StatusCodes.Status508LoopDetected,
                        $"More than {BridgeProtocol.MaxHops} bridges in a row (or bridges that lead back to each other).");
                }

                BridgeProtocol.CurrentHops.Value = hops;
                return await next(context);
            });

        bridge.MapGet("/environments", EnvironmentsAsync);
        bridge.MapGet("/info", InfoAsync);
        bridge.MapPost("/exec", ExecAsync);
    }

    private static async Task<IResult> EnvironmentsAsync(HttpContext http, EnvironmentService environments)
        => ApiResults.Ok((await environments.GetAllAsync(http.RequestAborted))
            .Select(e => new BridgeEnvironment(e.Id, e.Name, ConnectionNames.Of(e.ConnectionType), e.IsEnabled)).ToList());

    private static async Task<IResult> InfoAsync(HttpContext http, EnvironmentService environments, SelfContainer self)
    {
        var (env, error) = await ResolveAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var settings = environments.BuildSettings(env);
        if (!settings.IsBridge)
        {
            return ApiResults.Ok(new BridgeInfo(env.Name, ConnectionNames.Of(env.ConnectionType), env.UseSudo, env.DockerHost, self.Id, 0, AppVersion.Informational));
        }

        // A further bridge: what the end of the chain says, one bridge more.
        try
        {
            var next = await BridgeHttp.InfoAsync(settings, http.RequestAborted);
            return ApiResults.Ok(next with { Hops = next.Hops + 1 });
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return ApiResults.Error(StatusCodes.Status502BadGateway, $"The bridge behind '{env.Name}' does not answer: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a command on an environment: the body is the command and then its stdin, the answer the frames of its output (stdout as it comes, then stderr
    /// and the exit code). The body may be large (an archive for a restore), so it has no size limit here.
    /// </summary>
    private static async Task ExecAsync(HttpContext http, EnvironmentService environments, IHostSessionFactory hosts, ILoggerFactory logs)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }

        var (env, error) = await ResolveAsync(http, environments);
        if (env is null)
        {
            await error!.ExecuteAsync(http);
            return;
        }

        var ct = http.RequestAborted;
        var command = await BridgeProtocol.ReadCommandAsync(http.Request.Body, ct);
        if (command is null)
        {
            await ApiResults.BadRequest("The body does not start with a command.").ExecuteAsync(http);
            return;
        }

        var seconds = int.TryParse(http.Request.Headers[BridgeProtocol.TimeoutHeader], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 600;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 24 * 3600));

        http.Response.ContentType = "application/octet-stream";
        http.Response.Headers.CacheControl = "no-store";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var answer = http.Response.Body;

        try
        {
            using var session = hosts.Create(environments.BuildSettings(env));
            await session.ConnectAsync(ct);
            using var cmd = session.CreateCommand(command);
            cmd.CommandTimeout = timeout;
            cmd.BufferOutput = false;
            var started = cmd.BeginExecute();

            // The stdin as it arrives, while the output goes back as it comes.
            var input = cmd.CreateInputStream();
            var feed = Task.Run(async () =>
            {
                try
                {
                    await http.Request.Body.CopyToAsync(input, ct);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // the command stopped reading, or the caller went away
                }
                finally
                {
                    try { input.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                }
            }, ct);

            var buffer = new byte[32 * 1024];
            var stdout = cmd.OutputStream;
            int read;
            while ((read = await stdout.ReadAsync(buffer, ct)) > 0)
            {
                await BridgeProtocol.WriteFrameAsync(answer, BridgeProtocol.Frame.Stdout, buffer.AsMemory(0, read), ct);
            }

            await feed;
            cmd.EndExecute(started);
            if (!string.IsNullOrEmpty(cmd.Error))
            {
                await BridgeProtocol.WriteFrameAsync(answer, BridgeProtocol.Frame.Stderr, Encoding.UTF8.GetBytes(cmd.Error), ct);
            }

            var exit = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(exit, cmd.ExitStatus ?? -1);
            await BridgeProtocol.WriteFrameAsync(answer, BridgeProtocol.Frame.Exit, exit, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logs.CreateLogger(typeof(BridgeApi)).LogInformation(ex, "A bridge command on environment {Env} failed.", env.Name);
            var message = DockerErrorMessages.IsSshError(ex) ? DockerErrorMessages.DescribeSshError(ex) : ex.Message;
            await BridgeProtocol.WriteFrameAsync(answer, BridgeProtocol.Frame.Failure, Encoding.UTF8.GetBytes($"On '{env.Name}': {message}"), CancellationToken.None);
        }
    }

    /// <summary>The environment the request names (<c>?environment=</c>, its id or name): it must exist and be enabled.</summary>
    private static async Task<(DockerEnvironment? Environment, IResult? Error)> ResolveAsync(HttpContext http, EnvironmentService environments)
    {
        var selector = http.Request.Query["environment"].ToString().Trim();
        if (selector.Length == 0)
        {
            return (null, ApiResults.BadRequest("Name the environment: ?environment=<name or id>."));
        }

        return await MatDockApi.ResolveEnvironmentAsync(http, environments);
    }
}
