using System.Text.RegularExpressions;
using MatDock.Core.Images;
using MatDock.Core.Networks;
using MatDock.Core.Volumes;

namespace MatDock.Core.Containers;

/// <summary>What an ad-hoc <c>docker run -d</c> starts: image, optional name, published ports, environment, mounts, restart policy and network.</summary>
public sealed class ContainerRunSpec
{
    public string Image { get; set; } = string.Empty;
    public string? Name { get; set; }

    /// <summary><c>8080:80</c>, <c>127.0.0.1:8080:80</c> or <c>53:53/udp</c> per entry.</summary>
    public List<string> Ports { get; set; } = new();

    /// <summary><c>KEY=value</c> per entry.</summary>
    public List<string> Env { get; set; } = new();

    /// <summary>A named volume or an absolute host path, a colon, an absolute container path, optionally <c>:ro</c>.</summary>
    public List<string> Volumes { get; set; } = new();

    /// <summary><c>no</c> (default), <c>always</c>, <c>unless-stopped</c> or <c>on-failure</c>.</summary>
    public string Restart { get; set; } = "no";

    public string? Network { get; set; }
}

/// <summary>Validates a <see cref="ContainerRunSpec"/> and builds its command. Every value is checked first and then single-quoted.</summary>
public static partial class ContainerRunCommands
{
    private static readonly HashSet<string> RestartPolicies = new(StringComparer.Ordinal) { "no", "always", "unless-stopped", "on-failure" };

    [GeneratedRegex(@"^(\d{1,3}(\.\d{1,3}){3}:)?\d{1,5}:\d{1,5}(/(tcp|udp))?\z")]
    private static partial Regex PortRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex EnvKeyRegex();

    // Container path: absolute, no colon (it is the separator), no whitespace/control characters.
    [GeneratedRegex(@"^/[^\s:'""\\\x00-\x1f]*\z")]
    private static partial Regex ContainerPathRegex();

    /// <summary>Null when the spec is valid, otherwise the first problem in words.</summary>
    public static string? Validate(ContainerRunSpec spec)
    {
        if (!ImageCommands.IsValidImageRef(spec.Image))
        {
            return "Enter a valid image reference (e.g. nginx:alpine).";
        }
        if (!string.IsNullOrEmpty(spec.Name) && !ContainerCommands.IsValidId(spec.Name))
        {
            return "The container name may use letters, digits, . _ - (start alphanumeric).";
        }
        if (!RestartPolicies.Contains(spec.Restart))
        {
            return "Unknown restart policy.";
        }
        if (!string.IsNullOrEmpty(spec.Network) && !NetworkCommands.IsValidNetworkRef(spec.Network))
        {
            return "Invalid network name.";
        }

        foreach (var p in spec.Ports)
        {
            if (!PortRegex().IsMatch(p) || p.Split(':').Select(x => x.Split('/')[0]).Where(x => x.Contains('.') == false).Any(x => int.Parse(x) is < 1 or > 65535))
            {
                return $"Invalid port mapping '{p}' (expected 8080:80, 127.0.0.1:8080:80 or 53:53/udp).";
            }
        }

        foreach (var e in spec.Env)
        {
            var eq = e.IndexOf('=');
            if (eq <= 0 || !EnvKeyRegex().IsMatch(e[..eq]) || e.Contains('\n') || e.Contains('\r') || e.Contains('\0'))
            {
                return $"Invalid environment entry '{e}' (expected KEY=value).";
            }
        }

        foreach (var v in spec.Volumes)
        {
            if (ParseVolume(v) is null)
            {
                return $"Invalid volume '{v}' (expected name:/path, /host/path:/path or either with :ro).";
            }
        }

        return null;
    }

    /// <summary><c>docker run -d …</c> for a valid spec (throws <see cref="ArgumentException"/> otherwise).</summary>
    public static string Run(ContainerRunSpec spec, string dockerHead = "docker")
    {
        var error = Validate(spec);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(spec));
        }

        var q = (string s) => VolumeFileCommands.ShellQuote(s);
        var parts = new List<string> { dockerHead, "run", "-d" };
        if (!string.IsNullOrEmpty(spec.Name)) { parts.Add("--name " + q(spec.Name)); }
        if (spec.Restart != "no") { parts.Add("--restart " + q(spec.Restart)); }
        if (!string.IsNullOrEmpty(spec.Network)) { parts.Add("--network " + q(spec.Network)); }
        parts.AddRange(spec.Ports.Select(p => "-p " + q(p)));
        parts.AddRange(spec.Env.Select(e => "-e " + q(e)));
        parts.AddRange(spec.Volumes.Select(v => "-v " + q(v)));
        parts.Add(q(spec.Image));
        return VolumeCommands.PathPrefix + string.Join(' ', parts) + " 2>&1";
    }

    /// <summary>Splits <c>source:/target[:ro]</c>; null when it is not a named volume or absolute path mapped to an absolute container path.</summary>
    private static (string Source, string Target, bool ReadOnly)? ParseVolume(string v)
    {
        var parts = v.Split(':');
        var ro = parts.Length == 3 && parts[2] == "ro";
        if (parts.Length is < 2 or > 3 || (parts.Length == 3 && !ro))
        {
            return null;
        }

        var source = parts[0];
        var target = parts[1];
        var sourceOk = VolumeCommands.IsValidVolumeName(source)
                       || (source.StartsWith('/') && ContainerPathRegex().IsMatch(source));
        return sourceOk && target.Length > 1 && ContainerPathRegex().IsMatch(target) ? (source, target, ro) : null;
    }
}
