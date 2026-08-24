using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace MatDock.Core.Docker;

/// <summary>
/// Tunnels the Docker Engine API over SSH. A loopback <see cref="TcpListener"/> accepts connections
/// from the Docker.DotNet <see cref="HttpClient"/>; every accepted connection is bridged to a fresh
/// <c>docker system dial-stdio</c> channel on the remote host. From Docker.DotNet's point of view it
/// is talking plain HTTP to a local socket, while the bytes actually flow to the remote daemon.
/// </summary>
internal sealed class SshDockerHost : IDockerHost
{
    private readonly SshClient _ssh;
    private readonly TcpListener _listener;
    private readonly DockerClient _client;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private SshDockerHost(SshClient ssh, TcpListener listener, DockerClient client, ILogger logger)
    {
        _ssh = ssh;
        _listener = listener;
        _client = client;
        _logger = logger;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public IDockerClient Client => _client;

    /// <summary>Starts the proxy for an already-connected SSH client and returns a ready host.</summary>
    public static SshDockerHost Start(SshClient connectedSsh, TimeSpan dockerTimeout, ILogger logger)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var configuration = new DockerClientConfiguration(
            new Uri($"http://127.0.0.1:{port}"),
            defaultTimeout: dockerTimeout);
        var client = configuration.CreateClient();

        return new SshDockerHost(connectedSsh, listener, client, logger);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            _ = Task.Run(() => HandleConnectionAsync(tcp));
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp)
    {
        using (tcp)
        {
            tcp.NoDelay = true;
            SshDialStdioChannel? channel = null;
            try
            {
                channel = SshDialStdioChannel.Open(_ssh);
                var network = tcp.GetStream();

                var clientToDaemon = CopyAsync(network, channel.Input, _cts.Token);
                var daemonToClient = CopyAsync(channel.Output, network, _cts.Token);

                await Task.WhenAny(clientToDaemon, daemonToClient);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Docker-over-SSH proxy connection ended unexpectedly.");
            }
            finally
            {
                channel?.Dispose();
            }
        }
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await to.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (IOException) { /* peer closed the connection */ }
        catch (ObjectDisposedException) { /* peer closed the connection */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _client.Dispose();
        try { await _acceptLoop; } catch { /* ignore */ }
        try { if (_ssh.IsConnected) { _ssh.Disconnect(); } } catch { /* best effort */ }
        _ssh.Dispose();
        _cts.Dispose();
    }
}
