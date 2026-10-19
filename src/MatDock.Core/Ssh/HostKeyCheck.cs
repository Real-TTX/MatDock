namespace MatDock.Core.Ssh;

/// <summary>
/// The host key of an SSH connection: the fingerprint the server showed and whether it was refused because it differs
/// from the one saved for the environment (trust on first use: the first key is saved, a different one later is refused,
/// as a man in the middle would show one).
/// </summary>
public sealed class HostKeyCheck
{
    /// <summary>The fingerprint the server showed ("SHA256:…"), or null before the connection got that far.</summary>
    public string? Seen { get; set; }

    /// <summary>The server showed another key than the saved one; the connection was refused.</summary>
    public bool Rejected { get; set; }

    public static string Format(string sha256Base64) => "SHA256:" + sha256Base64.TrimEnd('=');

    /// <summary>Whether a server that shows <paramref name="seen"/> is trusted when <paramref name="expected"/> is saved.</summary>
    public static bool Trusts(string? expected, string seen)
        => string.IsNullOrEmpty(expected) || string.Equals(expected.TrimEnd('='), seen.TrimEnd('='), StringComparison.Ordinal);
}
