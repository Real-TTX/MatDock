using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MatDock.Core.Images;
using MatDock.Core.Networks;
using MatDock.Core.Volumes;

namespace MatDock.Core.Containers;

/// <summary>What an ad-hoc <c>docker run -d</c> starts. Everything but the image is optional.</summary>
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

    /// <summary>A fixed IPv4 or IPv6 address on <see cref="Network"/> (a user-defined network with a subnet).</summary>
    public string? Ip { get; set; }

    /// <summary>More names the container answers to on <see cref="Network"/>.</summary>
    public List<string> NetworkAliases { get; set; } = new();

    /// <summary>The arguments after the image (the command), one per entry; each is passed as it is.</summary>
    public List<string> Command { get; set; } = new();

    /// <summary>Replaces the entrypoint of the image (one program).</summary>
    public string? Entrypoint { get; set; }

    /// <summary><c>key=value</c> per entry.</summary>
    public List<string> Labels { get; set; } = new();

    /// <summary>Memory limit: <c>512m</c>, <c>2g</c>.</summary>
    public string? Memory { get; set; }

    /// <summary>CPU limit: <c>1.5</c> (cores).</summary>
    public string? Cpus { get; set; }

    public string? Hostname { get; set; }

    /// <summary><c>1000</c>, <c>app</c> or <c>1000:1000</c>.</summary>
    public string? User { get; set; }

    public string? WorkDir { get; set; }

    public bool Privileged { get; set; }

    /// <summary>Capabilities to add (<c>NET_ADMIN</c>) and to drop.</summary>
    public List<string> CapAdd { get; set; } = new();

    public List<string> CapDrop { get; set; } = new();

    /// <summary><c>/dev/ttyUSB0</c> or <c>/dev/ttyUSB0:/dev/ttyUSB0:rwm</c> per entry.</summary>
    public List<string> Devices { get; set; } = new();

    /// <summary><c>all</c>, a number, or <c>device=0,1</c>.</summary>
    public string? Gpus { get; set; }

    /// <summary><c>json-file</c>, <c>local</c>, <c>journald</c>, <c>syslog</c> or <c>none</c>; the size and number of files for the first two.</summary>
    public string? LogDriver { get; set; }

    public string? LogMaxSize { get; set; }

    public int? LogMaxFile { get; set; }

    /// <summary>DNS servers (addresses).</summary>
    public List<string> Dns { get; set; } = new();

    /// <summary><c>host:address</c> (or <c>host:host-gateway</c>) per entry.</summary>
    public List<string> ExtraHosts { get; set; } = new();

    /// <summary>A health check run in a shell inside the container, how often, and after how many failures it is unhealthy.</summary>
    public string? HealthCmd { get; set; }

    public string? HealthInterval { get; set; }

    public int? HealthRetries { get; set; }
}

/// <summary>What <c>docker update</c> changes on a container that exists: limits and restart policy. Null = unchanged.</summary>
public sealed record ContainerUpdateSpec(string? Memory, string? Cpus, string? Restart);

/// <summary>Validates a <see cref="ContainerRunSpec"/> and builds its command. Every value is checked first and then single-quoted.</summary>
public static partial class ContainerRunCommands
{
    public static readonly IReadOnlySet<string> RestartPolicies = new HashSet<string>(StringComparer.Ordinal) { "no", "always", "unless-stopped", "on-failure" };

    public static readonly IReadOnlySet<string> LogDrivers = new HashSet<string>(StringComparer.Ordinal) { "json-file", "local", "journald", "syslog", "none" };

    [GeneratedRegex(@"^(\d{1,3}(\.\d{1,3}){3}:)?\d{1,5}:\d{1,5}(/(tcp|udp))?\z")]
    private static partial Regex PortRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex EnvKeyRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,255}\z")]
    private static partial Regex LabelKeyRegex();

    // Container path: absolute, no colon (it is the separator), no whitespace/control characters.
    [GeneratedRegex(@"^/[^\s:'""\\\x00-\x1f]*\z")]
    private static partial Regex ContainerPathRegex();

    [GeneratedRegex(@"^\d{1,6}(\.\d{1,3})?[bkmg]?\z", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,2})?\z")]
    private static partial Regex CpusRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\.[A-Za-z0-9-]{1,63})*\z")]
    private static partial Regex HostnameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}(:[A-Za-z0-9_][A-Za-z0-9_.-]{0,31})?\z")]
    private static partial Regex UserRegex();

    [GeneratedRegex(@"^([A-Z][A-Z_]{1,31}|ALL)\z")]
    private static partial Regex CapabilityRegex();

    [GeneratedRegex(@"^/dev/[A-Za-z0-9_./-]+(:/dev/[A-Za-z0-9_./-]+)?(:[rwm]{1,3})?\z")]
    private static partial Regex DeviceRegex();

    [GeneratedRegex(@"^(all|\d{1,2}|device=[0-9]{1,2}(,[0-9]{1,2}){0,15})\z")]
    private static partial Regex GpusRegex();

    [GeneratedRegex(@"^\d{1,5}(ms|s|m|h)\z")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}\z")]
    private static partial Regex AliasRegex();

    [GeneratedRegex(@"^[^\x00-\x1f]*\z")]
    private static partial Regex NoControlRegex();

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
        if (!string.IsNullOrEmpty(spec.Ip) && (string.IsNullOrEmpty(spec.Network) || !IPAddress.TryParse(spec.Ip, out _)))
        {
            return "A fixed IP address needs a network and a valid address.";
        }
        if (spec.NetworkAliases.Count > 0 && string.IsNullOrEmpty(spec.Network))
        {
            return "Network aliases need a network.";
        }
        if (spec.NetworkAliases.FirstOrDefault(a => !AliasRegex().IsMatch(a)) is { } badAlias)
        {
            return $"Invalid network alias '{badAlias}'.";
        }

        foreach (var p in spec.Ports)
        {
            if (!PortRegex().IsMatch(p) || p.Split(':').Select(x => x.Split('/')[0]).Where(x => x.Contains('.') == false).Any(x => int.Parse(x, CultureInfo.InvariantCulture) is < 1 or > 65535))
            {
                return $"Invalid port mapping '{p}' (expected 8080:80, 127.0.0.1:8080:80 or 53:53/udp).";
            }
        }

        foreach (var e in spec.Env)
        {
            var eq = e.IndexOf('=');
            if (eq <= 0 || !EnvKeyRegex().IsMatch(e[..eq]) || !NoControlRegex().IsMatch(e))
            {
                return $"Invalid environment entry '{e}' (expected KEY=value).";
            }
        }

        foreach (var l in spec.Labels)
        {
            var eq = l.IndexOf('=');
            if (eq <= 0 || !LabelKeyRegex().IsMatch(l[..eq]) || !NoControlRegex().IsMatch(l))
            {
                return $"Invalid label '{l}' (expected key=value).";
            }
        }

        foreach (var v in spec.Volumes)
        {
            if (ParseVolume(v) is null)
            {
                return $"Invalid volume '{v}' (expected name:/path, /host/path:/path or either with :ro).";
            }
        }

        if (spec.Command.Any(a => !NoControlRegex().IsMatch(a)))
        {
            return "A command argument may not contain line breaks or control characters.";
        }
        if (!string.IsNullOrEmpty(spec.Entrypoint) && (spec.Entrypoint.Trim().Length == 0 || !NoControlRegex().IsMatch(spec.Entrypoint)))
        {
            return "Invalid entrypoint.";
        }
        if (!string.IsNullOrEmpty(spec.Memory) && !SizeRegex().IsMatch(spec.Memory))
        {
            return "Memory limit like 512m or 2g.";
        }
        if (!string.IsNullOrEmpty(spec.Cpus) && (!CpusRegex().IsMatch(spec.Cpus) || double.Parse(spec.Cpus, CultureInfo.InvariantCulture) <= 0))
        {
            return "CPU limit like 0.5 or 2.";
        }
        if (!string.IsNullOrEmpty(spec.Hostname) && !HostnameRegex().IsMatch(spec.Hostname))
        {
            return "Invalid hostname.";
        }
        if (!string.IsNullOrEmpty(spec.User) && !UserRegex().IsMatch(spec.User))
        {
            return "User like 1000, app or 1000:1000.";
        }
        if (!string.IsNullOrEmpty(spec.WorkDir) && !ContainerPathRegex().IsMatch(spec.WorkDir))
        {
            return "The working directory is an absolute path in the container.";
        }
        if (spec.CapAdd.Concat(spec.CapDrop).FirstOrDefault(c => !CapabilityRegex().IsMatch(c)) is { } badCap)
        {
            return $"Invalid capability '{badCap}' (like NET_ADMIN).";
        }
        if (spec.Devices.FirstOrDefault(d => !DeviceRegex().IsMatch(d)) is { } badDevice)
        {
            return $"Invalid device '{badDevice}' (like /dev/ttyUSB0 or /dev/ttyUSB0:/dev/ttyUSB0:rwm).";
        }
        if (!string.IsNullOrEmpty(spec.Gpus) && !GpusRegex().IsMatch(spec.Gpus))
        {
            return "GPUs: all, a number, or device=0,1.";
        }
        if (!string.IsNullOrEmpty(spec.LogDriver) && !LogDrivers.Contains(spec.LogDriver))
        {
            return "Unknown logging driver.";
        }
        if (!string.IsNullOrEmpty(spec.LogMaxSize) && (!SizeRegex().IsMatch(spec.LogMaxSize) || spec.LogDriver is not (null or "" or "json-file" or "local")))
        {
            return "The size of a log file like 10m, for json-file or local.";
        }
        if (spec.LogMaxFile is { } files && (files is < 1 or > 100 || spec.LogDriver is not (null or "" or "json-file" or "local")))
        {
            return "Between 1 and 100 log files, for json-file or local.";
        }
        if (spec.Dns.FirstOrDefault(d => !IPAddress.TryParse(d, out _)) is { } badDns)
        {
            return $"Invalid DNS server '{badDns}' (an address).";
        }
        foreach (var h in spec.ExtraHosts)
        {
            var colon = h.IndexOf(':');
            if (colon <= 0 || !HostnameRegex().IsMatch(h[..colon]) || !(h[(colon + 1)..] == "host-gateway" || IPAddress.TryParse(h[(colon + 1)..], out _)))
            {
                return $"Invalid extra host '{h}' (expected name:address or name:host-gateway).";
            }
        }
        if (!string.IsNullOrEmpty(spec.HealthCmd) && !NoControlRegex().IsMatch(spec.HealthCmd))
        {
            return "The health check is one line.";
        }
        if (!string.IsNullOrEmpty(spec.HealthInterval) && !DurationRegex().IsMatch(spec.HealthInterval))
        {
            return "Health check interval like 30s or 1m.";
        }
        if (spec.HealthRetries is { } retries && retries is < 1 or > 100)
        {
            return "Between 1 and 100 health check retries.";
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
        if (!string.IsNullOrEmpty(spec.Ip)) { parts.Add((spec.Ip.Contains(':') ? "--ip6 " : "--ip ") + q(spec.Ip)); }
        parts.AddRange(spec.NetworkAliases.Select(a => "--network-alias " + q(a)));
        parts.AddRange(spec.Ports.Select(p => "-p " + q(p)));
        parts.AddRange(spec.Env.Select(e => "-e " + q(e)));
        parts.AddRange(spec.Labels.Select(l => "--label " + q(l)));
        parts.AddRange(spec.Volumes.Select(v => "-v " + q(v)));
        if (!string.IsNullOrEmpty(spec.Entrypoint)) { parts.Add("--entrypoint " + q(spec.Entrypoint)); }
        if (!string.IsNullOrEmpty(spec.Memory)) { parts.Add("--memory " + q(spec.Memory.ToLowerInvariant())); }
        if (!string.IsNullOrEmpty(spec.Cpus)) { parts.Add("--cpus " + q(spec.Cpus)); }
        if (!string.IsNullOrEmpty(spec.Hostname)) { parts.Add("--hostname " + q(spec.Hostname)); }
        if (!string.IsNullOrEmpty(spec.User)) { parts.Add("--user " + q(spec.User)); }
        if (!string.IsNullOrEmpty(spec.WorkDir)) { parts.Add("--workdir " + q(spec.WorkDir)); }
        if (spec.Privileged) { parts.Add("--privileged"); }
        parts.AddRange(spec.CapAdd.Select(c => "--cap-add " + q(c)));
        parts.AddRange(spec.CapDrop.Select(c => "--cap-drop " + q(c)));
        parts.AddRange(spec.Devices.Select(d => "--device " + q(d)));
        if (!string.IsNullOrEmpty(spec.Gpus)) { parts.Add("--gpus " + q(spec.Gpus)); }
        if (!string.IsNullOrEmpty(spec.LogDriver)) { parts.Add("--log-driver " + q(spec.LogDriver)); }
        if (!string.IsNullOrEmpty(spec.LogMaxSize)) { parts.Add("--log-opt " + q("max-size=" + spec.LogMaxSize.ToLowerInvariant())); }
        if (spec.LogMaxFile is { } files) { parts.Add("--log-opt " + q("max-file=" + files.ToString(CultureInfo.InvariantCulture))); }
        parts.AddRange(spec.Dns.Select(d => "--dns " + q(d)));
        parts.AddRange(spec.ExtraHosts.Select(h => "--add-host " + q(h)));
        if (!string.IsNullOrEmpty(spec.HealthCmd)) { parts.Add("--health-cmd " + q(spec.HealthCmd)); }
        if (!string.IsNullOrEmpty(spec.HealthInterval)) { parts.Add("--health-interval " + q(spec.HealthInterval)); }
        if (spec.HealthRetries is { } retries) { parts.Add("--health-retries " + retries.ToString(CultureInfo.InvariantCulture)); }
        parts.Add(q(spec.Image));
        parts.AddRange(spec.Command.Select(q));
        return VolumeCommands.PathPrefix + string.Join(' ', parts) + " 2>&1";
    }

    /// <summary>Null when the update is valid (and changes something), otherwise the problem.</summary>
    public static string? Validate(ContainerUpdateSpec update)
    {
        if (string.IsNullOrEmpty(update.Memory) && string.IsNullOrEmpty(update.Cpus) && string.IsNullOrEmpty(update.Restart))
        {
            return "Nothing to change.";
        }
        if (!string.IsNullOrEmpty(update.Memory) && update.Memory != "0" && !SizeRegex().IsMatch(update.Memory))
        {
            return "Memory limit like 512m or 2g (0 = no limit).";
        }
        if (!string.IsNullOrEmpty(update.Cpus) && update.Cpus != "0" && (!CpusRegex().IsMatch(update.Cpus) || double.Parse(update.Cpus, CultureInfo.InvariantCulture) <= 0))
        {
            return "CPU limit like 0.5 or 2 (0 = no limit).";
        }
        if (!string.IsNullOrEmpty(update.Restart) && !RestartPolicies.Contains(update.Restart))
        {
            return "Unknown restart policy.";
        }

        return null;
    }

    /// <summary><c>docker update</c> of limits and restart policy. A memory limit also sets the swap to unlimited, so a lower or higher limit is always accepted.</summary>
    public static string Update(string dockerHead, string id, ContainerUpdateSpec update)
    {
        if (!ContainerCommands.IsValidId(id) || Validate(update) is { } error)
        {
            throw new ArgumentException(ContainerCommands.IsValidId(id) ? Validate(update) : $"Invalid container ID: '{id}'.");
        }

        var q = (string s) => VolumeFileCommands.ShellQuote(s);
        var parts = new List<string> { dockerHead, "update" };
        if (!string.IsNullOrEmpty(update.Memory)) { parts.Add("--memory " + q(update.Memory.ToLowerInvariant()) + " --memory-swap -1"); }
        if (!string.IsNullOrEmpty(update.Cpus)) { parts.Add("--cpus " + q(update.Cpus)); }
        if (!string.IsNullOrEmpty(update.Restart)) { parts.Add("--restart " + q(update.Restart)); }
        parts.Add(q(id));
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
