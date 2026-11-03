using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Security.Authentication;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MatDock.Core.Ssh;

namespace MatDock.Core.Execution;

/// <summary>
/// How two MatDocks talk when one is the bridge of the other (<see cref="Entities.ConnectionType.Bridge"/>). The calling MatDock sends a command as it would
/// run it over SSH; the other runs it on one of its environments and answers with what the command wrote and its exit code. Over HTTPS, with a key of
/// the scope <see cref="Entities.ApiKeyScope.Bridge"/>.
/// <list type="bullet">
/// <item><c>POST /api/v1/bridge/exec?environment=</c>: the body is the command (a 4-byte length, big-endian, then UTF-8) followed by what goes to its
/// stdin; the answer is a row of frames (<see cref="Frame"/>): stdout as it comes, then stderr, then the exit code.</item>
/// <item><c>GET /api/v1/bridge/info?environment=</c>: how Docker is called there (sudo, DOCKER_HOST) and which container is the MatDock on that Docker
/// (<see cref="BridgeInfo"/>), through every further bridge to the end.</item>
/// <item><c>GET /api/v1/bridge/environments</c>: the environments that can be named.</item>
/// </list>
/// </summary>
public static class BridgeProtocol
{
    public const string ExecPath = "/api/v1/bridge/exec";
    public const string InfoPath = "/api/v1/bridge/info";
    public const string EnvironmentsPath = "/api/v1/bridge/environments";

    /// <summary>How long the command may take, in seconds (the other side ends it then).</summary>
    public const string TimeoutHeader = "X-MatDock-Timeout";

    /// <summary>How many bridges the request has passed already: a chain of more than <see cref="MaxHops"/> is refused (and a loop with it).</summary>
    public const string HopsHeader = "X-MatDock-Hops";

    public const int MaxHops = 4;

    /// <summary>The longest command a bridge takes (the scripts with their arguments are a few kilobytes).</summary>
    public const int MaxCommandBytes = 1024 * 1024;

    public enum Frame : byte
    {
        Stdout = 1,
        Stderr = 2,

        /// <summary>The exit code (4 bytes, big-endian); the last frame.</summary>
        Exit = 3,

        /// <summary>The command could not be run there (the environment unreachable …): a message, then the end.</summary>
        Failure = 4,
    }

    public static async Task WriteFrameAsync(Stream stream, Frame type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var head = new byte[5];
        head[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(1), payload.Length);
        await stream.WriteAsync(head, ct);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, ct);
        }

        await stream.FlushAsync(ct);
    }

    /// <summary>The next frame, or null at the end of the stream.</summary>
    public static async Task<(Frame Type, byte[] Payload)?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[5];
        var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (read == 0)
        {
            return null;
        }

        if (read < head.Length)
        {
            throw new IOException("The bridge answer ended in the middle of a frame.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1));
        if (length is < 0 or > 16 * 1024 * 1024)
        {
            throw new IOException("The bridge answer is not a frame.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return ((Frame)head[0], payload);
    }

    public static byte[] CommandHead(string command)
    {
        var text = Encoding.UTF8.GetBytes(command);
        var head = new byte[4 + text.Length];
        BinaryPrimitives.WriteInt32BigEndian(head, text.Length);
        text.CopyTo(head, 4);
        return head;
    }

    /// <summary>Reads the command at the start of a body; null when it is not one.</summary>
    public static async Task<string?> ReadCommandAsync(Stream body, CancellationToken ct)
    {
        var length = new byte[4];
        if (await body.ReadAtLeastAsync(length, 4, throwOnEndOfStream: false, ct) < 4)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size is <= 0 or > MaxCommandBytes)
        {
            return null;
        }

        var text = new byte[size];
        await body.ReadExactlyAsync(text, ct);
        return Encoding.UTF8.GetString(text);
    }

    /// <summary>The SHA-256 of a certificate as MatDock shows it: hex pairs with colons.</summary>
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData)).Chunk(2).Select(p => new string(p)).Aggregate((a, b) => a + ":" + b);

    /// <summary>A fingerprint without colons, spaces and case, to compare.</summary>
    public static string NormalizeFingerprint(string? fingerprint)
        => new string((fingerprint ?? string.Empty).Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Whether an address can be a bridge: http or https, a host, no user or password in it.</summary>
    public static string? Problem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host))
        {
            return "The address of the other MatDock is an http(s) address, such as https://server2:4455.";
        }

        return string.IsNullOrEmpty(uri.UserInfo) ? null : "No user or password in the address: the key goes in its own field.";
    }

    /// <summary>The chain the current request came through (<see cref="HopsHeader"/>), so a further bridge counts on.</summary>
    public static readonly AsyncLocal<int> CurrentHops = new();
}

/// <summary>
/// What a bridge says about one of its environments, through every further bridge to the end: how Docker is called there and which container is the
/// MatDock on that Docker (it must not be stopped from here, and steps of its own stack go to a helper container, <see cref="Stacks.SelfHandover"/>).
/// </summary>
public sealed record BridgeInfo(string Environment, string Connection, bool UseSudo, string? DockerHost, string? MatDockContainer, int Hops, string? Version = null);

/// <summary>An environment a bridge offers.</summary>
public sealed record BridgeEnvironment(long Id, string Name, string Connection, bool Enabled);

/// <summary>The HTTP side of a bridge: one client per trusted certificate, the calls for the info and the list.</summary>
public static class BridgeHttp
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new(StringComparer.Ordinal);

    /// <summary>A test can put its own handler here (the other MatDock in memory).</summary>
    public static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    public static HttpClient Client(string? certificatePin)
    {
        var pin = BridgeProtocol.NormalizeFingerprint(certificatePin);
        if (HandlerForTests is { } make)
        {
            return new HttpClient(make(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }

        return Clients.GetOrAdd(pin, p =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AutomaticDecompression = DecompressionMethods.None,
            };
            if (p.Length > 0)
            {
                // A certificate no CA vouches for (a self-signed one): trusted when it is exactly the one the user pinned.
                handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors)
                    => errors == SslPolicyErrors.None || (cert is not null && BridgeProtocol.NormalizeFingerprint(BridgeProtocol.Fingerprint(new X509Certificate2(cert))) == p);
            }

            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        });
    }

    public static Uri Address(SshConnectionSettings settings, string path, bool withEnvironment = true)
    {
        var root = settings.Host.Trim().TrimEnd('/');
        var query = withEnvironment ? "?environment=" + Uri.EscapeDataString(settings.RemoteEnvironment ?? string.Empty) : string.Empty;
        return new Uri(root + path + query);
    }

    public static HttpRequestMessage Request(HttpMethod method, Uri uri, SshConnectionSettings settings)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-Api-Key", settings.Password ?? string.Empty);
        request.Headers.TryAddWithoutValidation(BridgeProtocol.HopsHeader, (BridgeProtocol.CurrentHops.Value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }

    public static async Task<BridgeInfo> InfoAsync(SshConnectionSettings settings, CancellationToken ct)
        => await GetAsync<BridgeInfo>(settings, Address(settings, BridgeProtocol.InfoPath), ct);

    public static async Task<IReadOnlyList<BridgeEnvironment>> EnvironmentsAsync(SshConnectionSettings settings, CancellationToken ct)
        => await GetAsync<List<BridgeEnvironment>>(settings, Address(settings, BridgeProtocol.EnvironmentsPath, withEnvironment: false), ct);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<T> GetAsync<T>(SshConnectionSettings settings, Uri uri, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)));
        using var request = Request(HttpMethod.Get, uri, settings);
        using var response = await Client(settings.HostKeyFingerprint).SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException(await DescribeAsync(response, cts.Token));
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cts.Token) ?? throw new IOException("The other MatDock gave an empty answer.");
    }

    /// <summary>A refused request in words: the other MatDock's own message when it sent one.</summary>
    public static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? said = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            said = JsonDocument.Parse(text).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
        {
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The other MatDock does not accept the bridge key.",
            HttpStatusCode.Forbidden => said ?? "The key is not a bridge key (System → API keys, \"Bridge\").",
            HttpStatusCode.NotFound => said ?? "The other MatDock has no bridge (an older version?) or no such environment.",
            HttpStatusCode.ServiceUnavailable => said ?? "The API of the other MatDock is off.",
            _ => said ?? $"The other MatDock answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
    }

    /// <summary>The certificate an https address shows, whoever vouches for it (to name it when it is not trusted); null when none can be read.</summary>
    public static async Task<string?> PeekCertificateAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            return null;
        }

        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(uri.Host, uri.Port, cts.Token);
            string? seen = null;
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            {
                seen = cert is null ? null : BridgeProtocol.Fingerprint(new X509Certificate2(cert));
                return true;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, cts.Token);
            return seen;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or AuthenticationException)
        {
            return null;
        }
    }
}

/// <summary>A session through a bridge: nothing to open, every command is one request.</summary>
internal sealed class BridgeHostSession(SshConnectionSettings settings) : IHostSession
{
    public Task ConnectAsync(CancellationToken ct)
        => BridgeProtocol.Problem(settings.Host) is { } problem ? Task.FromException(new IOException(problem)) : Task.CompletedTask;

    public IHostCommand CreateCommand(string command) => new BridgeHostCommand(settings, command);

    public void Dispose()
    {
    }
}

/// <summary>
/// One command through a bridge. Like a local command: <see cref="BeginExecute"/> sends it, <see cref="CreateInputStream"/> is its stdin (closed = the end
/// of the body), <see cref="OutputStream"/> its stdout as it comes. Without stdin the body ends when the output is read or the command awaited.
/// </summary>
internal sealed class BridgeHostCommand(SshConnectionSettings settings, string command) : IHostCommand
{
    private readonly Pipe _stdin = new();
    private readonly MemoryStream _buffered = new();
    private readonly StringBuilder _stderr = new();
    private Pipe? _stdout;
    private Task? _run;
    private CancellationTokenSource? _cts;
    private int _stdinState; // 0 = untouched, 1 = handed out, 2 = completed

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public int? ExitStatus { get; private set; }

    public string Error { get; private set; } = string.Empty;

    public string Result { get; private set; } = string.Empty;

    public bool BufferOutput { get; set; } = true;

    public Stream OutputStream
    {
        get
        {
            if (BufferOutput || _stdout is null)
            {
                throw new InvalidOperationException("OutputStream requires BufferOutput=false and a started command.");
            }

            EndStdinIfUnused();
            return _stdout.Reader.AsStream();
        }
    }

    public string Execute()
    {
        BufferOutput = true;
        EndExecute(BeginExecute());
        return Result;
    }

    public Task ExecuteAsync(CancellationToken ct) => Task.Run(() =>
    {
        BufferOutput = true;
        EndExecute(BeginExecute());
    }, ct);

    public IAsyncResult BeginExecute()
    {
        _stdout = BufferOutput ? null : new Pipe();
        _cts = new CancellationTokenSource(CommandTimeout <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : CommandTimeout + TimeSpan.FromSeconds(30));
        _run = Task.Run(() => RunAsync(_cts.Token));
        return _run;
    }

    public Stream CreateInputStream()
    {
        Interlocked.CompareExchange(ref _stdinState, 1, 0);
        return _stdin.Writer.AsStream(); // closing it ends the body
    }

    public void EndExecute(IAsyncResult asyncResult)
    {
        EndStdinIfUnused();
        if (_run is null)
        {
            return;
        }

        try
        {
            _run.Wait(CommandTimeout <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : CommandTimeout + TimeSpan.FromSeconds(35));
        }
        catch (AggregateException)
        {
            // recorded in Error by RunAsync
        }

        ExitStatus ??= -1;
    }

    private void EndStdinIfUnused()
    {
        if (Interlocked.CompareExchange(ref _stdinState, 2, 0) == 0)
        {
            _stdin.Writer.Complete();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var request = BridgeHttp.Request(HttpMethod.Post, BridgeHttp.Address(settings, BridgeProtocol.ExecPath), settings);
            request.Headers.TryAddWithoutValidation(BridgeProtocol.TimeoutHeader,
                ((int)Math.Max(1, CommandTimeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Content = new CommandContent(command, _stdin.Reader);
            using var response = await BridgeHttp.Client(settings.HostKeyFingerprint).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _stderr.Append(await BridgeHttp.DescribeAsync(response, ct));
                ExitStatus = -1;
                return;
            }

            await using var answer = await response.Content.ReadAsStreamAsync(ct);
            while (await BridgeProtocol.ReadFrameAsync(answer, ct) is { } frame)
            {
                switch (frame.Type)
                {
                    case BridgeProtocol.Frame.Stdout when _stdout is not null:
                        await _stdout.Writer.WriteAsync(frame.Payload, ct);
                        break;
                    case BridgeProtocol.Frame.Stdout:
                        _buffered.Write(frame.Payload);
                        break;
                    case BridgeProtocol.Frame.Stderr:
                        _stderr.Append(Encoding.UTF8.GetString(frame.Payload));
                        break;
                    case BridgeProtocol.Frame.Exit when frame.Payload.Length == 4:
                        ExitStatus = BinaryPrimitives.ReadInt32BigEndian(frame.Payload);
                        break;
                    case BridgeProtocol.Frame.Failure:
                        _stderr.Append(Encoding.UTF8.GetString(frame.Payload));
                        ExitStatus = -1;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _stderr.Append(ex is OperationCanceledException ? "The command through the bridge took too long." : Describe(ex));
            ExitStatus ??= -1;
        }
        finally
        {
            Error = _stderr.ToString();
            Result = BufferOutput ? Encoding.UTF8.GetString(_buffered.ToArray()) : string.Empty;
            _stdout?.Writer.Complete();
            // A body that was never ended (the request failed early) must not keep a writer waiting.
            _stdin.Reader.Complete();
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: AuthenticationException } =>
            "The certificate of the other MatDock is not trusted. If it is self-signed, enter its SHA-256 fingerprint in the environment.",
        HttpRequestException h => $"The other MatDock cannot be reached: {h.Message}",
        _ => ex.Message,
    };

    public void Dispose()
    {
        EndStdinIfUnused();
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
    }

    /// <summary>The body: the command, then the stdin as it is written.</summary>
    private sealed class CommandContent(string command, PipeReader stdin) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(BridgeProtocol.CommandHead(command));
            await stdin.CopyToAsync(stream);
            await stdin.CompleteAsync();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
