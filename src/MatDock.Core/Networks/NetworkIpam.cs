using System.Net;
using System.Text.RegularExpressions;

namespace MatDock.Core.Networks;

/// <summary>
/// The addresses of a new network: an IPv4 subnet with its gateway and the range Docker hands out, IPv6 with its own subnet, and for macvlan and ipvlan the
/// interface of the host it sits on. Everything optional; Docker picks a subnet itself when none is given.
/// </summary>
public sealed partial record NetworkIpam(string? Subnet, string? Gateway, string? IpRange, bool Ipv6, string? Ipv6Subnet, string? Parent)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,14}\z")]
    private static partial Regex InterfaceRegex();

    public bool IsEmpty => string.IsNullOrEmpty(Subnet) && string.IsNullOrEmpty(Gateway) && string.IsNullOrEmpty(IpRange) && !Ipv6 && string.IsNullOrEmpty(Parent);

    /// <summary>The problem with the values, or null when they fit together.</summary>
    public string? Problem(string? driver)
    {
        IPNetwork? subnet = null;
        if (!string.IsNullOrEmpty(Subnet))
        {
            if (!IPNetwork.TryParse(Subnet, out var parsed) || parsed.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return "The subnet is an IPv4 network like 172.30.0.0/24.";
            }

            subnet = parsed;
        }

        if (!string.IsNullOrEmpty(Gateway))
        {
            if (subnet is null)
            {
                return "A gateway needs a subnet.";
            }
            if (!IPAddress.TryParse(Gateway, out var gateway) || !subnet.Value.Contains(gateway))
            {
                return "The gateway is an address inside the subnet.";
            }
        }

        if (!string.IsNullOrEmpty(IpRange))
        {
            if (subnet is null)
            {
                return "An address range needs a subnet.";
            }
            if (!IPNetwork.TryParse(IpRange, out var range) || !subnet.Value.Contains(range.BaseAddress) || range.PrefixLength < subnet.Value.PrefixLength)
            {
                return "The address range is a network inside the subnet, like 172.30.0.128/25.";
            }
        }

        if (!string.IsNullOrEmpty(Ipv6Subnet))
        {
            if (!Ipv6)
            {
                return "An IPv6 subnet needs IPv6 switched on.";
            }
            if (!IPNetwork.TryParse(Ipv6Subnet, out var v6) || v6.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return "The IPv6 subnet is a network like fd00:dead:beef::/64.";
            }
        }

        if (!string.IsNullOrEmpty(Parent))
        {
            if (driver is not ("macvlan" or "ipvlan"))
            {
                return "A parent interface is only for macvlan and ipvlan.";
            }
            if (!InterfaceRegex().IsMatch(Parent))
            {
                return "The parent is the name of a network interface of the host, like eth0.";
            }
        }

        return null;
    }

    /// <summary>The arguments for <c>docker network create</c> (each value checked by <see cref="Problem"/> and quoted).</summary>
    public string Arguments()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Subnet)) { parts.Add("--subnet '" + Subnet + "'"); }
        if (!string.IsNullOrEmpty(Gateway)) { parts.Add("--gateway '" + Gateway + "'"); }
        if (!string.IsNullOrEmpty(IpRange)) { parts.Add("--ip-range '" + IpRange + "'"); }
        if (Ipv6) { parts.Add("--ipv6"); }
        if (!string.IsNullOrEmpty(Ipv6Subnet)) { parts.Add("--subnet '" + Ipv6Subnet + "'"); }
        if (!string.IsNullOrEmpty(Parent)) { parts.Add("-o 'parent=" + Parent + "'"); }
        return parts.Count == 0 ? string.Empty : " " + string.Join(' ', parts);
    }
}
