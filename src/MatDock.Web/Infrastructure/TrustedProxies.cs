using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Who may say through <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> where a request came from. <c>MatDock__TrustedProxies</c> is a
/// comma list of addresses or networks (<c>172.18.0.0/16, 10.0.0.5</c>): then only those (and the loopback) are believed. Unset = every
/// sender is believed, as before — fine behind a proxy that always overwrites the headers, but anyone who reaches MatDock directly can
/// claim another address (which weakens the rate limits of the sign-in and of the anonymous platform endpoints).
/// </summary>
public static class TrustedProxies
{
    public const string Setting = "MatDock:TrustedProxies";

    /// <summary>Applies <paramref name="list"/>; answers the problem of an entry that is no address or network, or null.</summary>
    public static string? Apply(ForwardedHeadersOptions options, string? list)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        if (string.IsNullOrWhiteSpace(list))
        {
            return null; // trust every sender (the behaviour before the setting existed)
        }

        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("127.0.0.0/8"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("::1/128"));
        foreach (var entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/') && System.Net.IPNetwork.TryParse(entry, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else
            {
                return $"{Setting}: '{entry}' is neither an address nor a network.";
            }
        }

        return null;
    }
}
