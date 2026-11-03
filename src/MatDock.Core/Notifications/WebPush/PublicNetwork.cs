using System.Net;
using System.Net.Sockets;

namespace MatDock.Core.Notifications.WebPush;

/// <summary>
/// Connections that may only lead to the INTERNET: not this machine, not a private network, not link-local, not multicast.
/// Used where an address comes from someone else - a browser's push endpoint
/// - so that naming an address cannot make this process reach into the network it runs in. The check is made on
/// the address actually connected to (after DNS, per connection, redirects included), so DNS rebinding does not get round it.
/// </summary>
public static class PublicNetwork
{
    /// <summary>A <see cref="SocketsHttpHandler.ConnectCallback"/> that connects to a public address only.</summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectCallback(string refusal) =>
        (context, cancellationToken) => ConnectAsync(context, refusal, cancellationToken);

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, string refusal, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var resolved = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var candidates = resolved.Where(IsPublic).ToArray();
        if (candidates.Length == 0)
        {
            throw new HttpRequestException(refusal);
        }

        SocketException? last = null;
        foreach (var target in candidates)
        {
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"{host} could not be reached.", last);
    }

    /// <summary>An address of the internet: not this machine, not a private network, not link-local, not multicast.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
    }
}
