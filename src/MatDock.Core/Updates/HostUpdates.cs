using System.Globalization;
using System.Text.RegularExpressions;
using MatDock.Core.Volumes;

namespace MatDock.Core.Updates;

/// <summary>One package of the host that apt would upgrade (or newly install, e.g. a new kernel: <see cref="From"/> is null).</summary>
/// <param name="Origin">The archive it comes from, as apt names it (<c>Debian-Security:13/stable-security</c>, <c>Docker CE:trixie</c>).</param>
public sealed record HostPackageUpdate(string Name, string? From, string To, string Origin)
{
    /// <summary>The packages of Docker itself (engine, CLI, containerd, plugins), from Docker's repository or the distribution's.</summary>
    public static readonly IReadOnlySet<string> DockerPackages = new HashSet<string>(StringComparer.Ordinal)
    {
        "docker-ce", "docker-ce-cli", "docker-ce-rootless-extras", "containerd.io", "docker-buildx-plugin",
        "docker-compose-plugin", "docker-model-plugin", "docker.io", "docker-cli", "docker-buildx", "docker-compose",
        "docker-compose-v2", "containerd", "runc",
    };

    public bool IsSecurity => Origin.Contains("-security", StringComparison.OrdinalIgnoreCase)
                              || Origin.Contains("Security:", StringComparison.OrdinalIgnoreCase);

    public bool IsDocker => DockerPackages.Contains(Name);

    /// <summary>A package that was not there before (a new kernel, a new dependency).</summary>
    public bool IsNew => From is null;
}

/// <summary>
/// What the host of an environment would get from apt: the packages, which of them are Docker's or security fixes, how old the package
/// lists are and whether the host waits for a reboot. Read by a throwaway container that looks at the host's own apt (see
/// <see cref="HostUpdateCommands"/>), so it works the same for a Local environment (MatDock in a container) and over SSH.
/// </summary>
public sealed partial record HostUpdateReport(
    string? OsName,
    bool AptAvailable,
    DateTime? ListsUpdatedUtc,
    bool RebootRequired,
    IReadOnlyList<HostPackageUpdate> Packages,
    string? Error)
{
    /// <summary>Package lists older than this make "nothing to update" a guess rather than a fact.</summary>
    public static readonly TimeSpan StaleLists = TimeSpan.FromDays(2);

    public IReadOnlyList<HostPackageUpdate> DockerPackages => Packages.Where(p => p.IsDocker).ToList();

    public int SecurityCount => Packages.Count(p => p.IsSecurity);

    /// <summary>The version of Docker Engine the update brings (from <c>docker-ce</c> or <c>docker.io</c>), shortened like <c>docker version</c> says it.</summary>
    public string? DockerTarget => Packages.FirstOrDefault(p => p.Name is "docker-ce" or "docker.io") is { } engine ? ShortVersion(engine.To) : null;

    public bool ListsStale(DateTime nowUtc) => AptAvailable && (ListsUpdatedUtc is null || nowUtc - ListsUpdatedUtc.Value > StaleLists);

    public static HostUpdateReport Failed(string error) => new(null, false, null, false, Array.Empty<HostPackageUpdate>(), error);

    /// <summary>One line for lists and notifications: <c>Docker 29.7.2 → 29.9.3 · 62 packages, 24 security · reboot required</c>.</summary>
    public string Summary(string? currentDocker)
    {
        if (Error is not null)
        {
            return Error;
        }

        if (!AptAvailable)
        {
            return "No apt on this host.";
        }

        var parts = new List<string>();
        if (DockerTarget is { } target)
        {
            parts.Add(string.IsNullOrEmpty(currentDocker) ? $"Docker {target}" : $"Docker {currentDocker} → {target}");
        }

        if (Packages.Count > 0)
        {
            parts.Add(SecurityCount > 0 ? $"{Packages.Count} packages, {SecurityCount} security" : $"{Packages.Count} packages");
        }

        if (RebootRequired)
        {
            parts.Add("reboot required");
        }

        return parts.Count == 0 ? "Up to date." : string.Join(" · ", parts);
    }

    /// <summary><c>5:29.9.3-1~debian.13~trixie</c> → <c>29.9.3</c>, <c>26.1.5+dfsg1-9+b9</c> → <c>26.1.5</c>: no epoch, no Debian revision, no repack suffix.</summary>
    public static string ShortVersion(string debianVersion)
    {
        var version = debianVersion;
        var colon = version.IndexOf(':');
        if (colon >= 0)
        {
            version = version[(colon + 1)..];
        }

        var cut = version.IndexOfAny(['-', '+', '~']);
        return cut > 0 ? version[..cut] : version;
    }

    /// <summary>Reads the output of <see cref="HostUpdateCommands.Check"/>.</summary>
    public static HostUpdateReport Parse(string output, string? stderr = null)
    {
        string? os = null;
        var apt = true;
        var sawOs = false;
        DateTime? lists = null;
        var reboot = false;
        var packages = new List<HostPackageUpdate>();
        var errors = new List<string>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("os|", StringComparison.Ordinal))
            {
                sawOs = true;
                var name = line[3..].Trim();
                os = name.Length == 0 ? null : name;
            }
            else if (line.StartsWith("noapt|", StringComparison.Ordinal))
            {
                apt = false;
            }
            else if (line.StartsWith("lists|", StringComparison.Ordinal))
            {
                if (long.TryParse(line[6..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    lists = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                }
            }
            else if (line.StartsWith("reboot|", StringComparison.Ordinal))
            {
                reboot = true;
            }
            else if (line.StartsWith("Inst ", StringComparison.Ordinal))
            {
                var match = InstLine().Match(line);
                if (match.Success)
                {
                    var from = match.Groups["from"].Success ? match.Groups["from"].Value : null;
                    packages.Add(new HostPackageUpdate(match.Groups["name"].Value, string.IsNullOrEmpty(from) ? null : from,
                        match.Groups["to"].Value, match.Groups["origin"].Value.Trim()));
                }
            }
            else if (line.StartsWith("E: ", StringComparison.Ordinal))
            {
                errors.Add(line[3..].Trim());
            }
        }

        if (!sawOs)
        {
            // The helper did not even start (no Docker, no image, a host without /bin/sh): say what the host said.
            var said = (stderr ?? string.Empty).Trim();
            return Failed(said.Length > 0 ? said : "The host did not answer the update check.");
        }

        return new HostUpdateReport(os, apt, lists, reboot, packages, errors.Count > 0 ? string.Join(" ", errors.Distinct()) : null);
    }

    // Inst <name> [<installed>] (<candidate> <archive(s)> [<arch>]) [...]; a new package has no [<installed>].
    [GeneratedRegex(@"^Inst (?<name>\S+)(?: \[(?<from>[^\]]*)\])? \((?<to>\S+) (?<origin>[^\[]*?) ?\[[^\]]+\]\)")]
    private static partial Regex InstLine();
}

/// <summary>
/// The commands behind host updates. Each runs a throwaway helper container through the environment's Docker, because a Local
/// environment's MatDock lives in a container and can reach its host only that way:
/// <list type="bullet">
/// <item><see cref="Check"/> refreshes the host's apt package lists (as <c>apt-get update</c> would) and asks apt what an upgrade would
/// install, without installing anything: the host's root is mounted read-only, only the package lists and apt's cache are writable.</item>
/// <item><see cref="StartUpgrade"/> hands the upgrade to the host's systemd as a transient unit, so it runs to its end even when the
/// upgrade restarts Docker (and with it the helper and, on a Local environment, MatDock).</item>
/// <item><see cref="ReadUpgrade"/> reads that unit's log and exit code, which it writes to <c>/var/log/matdock/</c>.</item>
/// </list>
/// </summary>
public static partial class HostUpdateCommands
{
    /// <summary>Where an upgrade writes its log (<c>&lt;unit&gt;.log</c>) and exit code (<c>&lt;unit&gt;.exit</c>) on the host.</summary>
    public const string LogDirectory = "/var/log/matdock";

    // Debian package names: lowercase letters, digits, + - . (at least two characters, starting alphanumeric).
    [GeneratedRegex(@"^[a-z0-9][a-z0-9+.-]+\z")]
    private static partial Regex PackageNameRegex();

    [GeneratedRegex(@"^matdock-host-update-[0-9a-f]{12}\z")]
    private static partial Regex UnitNameRegex();

    public static bool IsValidPackageName(string? name) => !string.IsNullOrEmpty(name) && PackageNameRegex().IsMatch(name);

    public static bool IsValidUnitName(string? unit) => !string.IsNullOrEmpty(unit) && UnitNameRegex().IsMatch(unit);

    /// <summary>A unique name for an upgrade's systemd unit (and its files in <see cref="LogDirectory"/>).</summary>
    public static string NewUnitName() => $"matdock-host-update-{Guid.NewGuid():N}"[..32];

    // Runs on the host's own userland (chroot), so stat/find/apt are the host's. LC_ALL=C keeps apt's output parseable
    // whatever language the host speaks. The lists' age is the newer of apt's own success stamp and the newest Release file.
    private const string CheckScript =
        "export LC_ALL=C; " +
        ". /etc/os-release 2>/dev/null; echo \"os|$PRETTY_NAME\"; " +
        "command -v apt-get >/dev/null 2>&1 || { echo 'noapt|'; exit 0; }; " +
        "if [ \"$REFRESH\" = 1 ]; then apt-get update -qq 2>&1 | grep '^E:'; fi; " +
        "echo \"lists|$(find /var/lib/apt/lists /var/lib/apt/periodic -maxdepth 1 \\( -name '*Release' -o -name update-success-stamp \\) -printf '%T@\\n' 2>/dev/null | sort -n | tail -n1 | cut -d. -f1)\"; " +
        "[ -f /var/run/reboot-required ] && echo 'reboot|1'; " +
        "apt-get -s -o Debug::NoLocking=1 dist-upgrade 2>&1 | grep -E '^(Inst|E:) '; " +
        "exit 0";

    /// <summary>
    /// Asks the host's apt what an upgrade would install. With <paramref name="refresh"/> it first fetches the package lists (needs the
    /// host's network, hence <c>--network host</c>: the host's resolver may listen on 127.0.0.53).
    /// </summary>
    public static string Check(string helperImage, string dockerHead = "docker", bool refresh = true)
    {
        EnsureImage(helperImage);
        return VolumeCommands.PathPrefix + dockerHead
               + " run --rm --log-driver none --network host"
               + " -v /:/host:ro -v /var/lib/apt/lists:/host/var/lib/apt/lists -v /var/cache/apt:/host/var/cache/apt --tmpfs /host/tmp"
               + $" -e REFRESH={(refresh ? "1" : "0")} "
               + VolumeFileCommands.ShellQuote(helperImage) + " chroot /host /bin/sh -c " + VolumeFileCommands.ShellQuote(CheckScript);
    }

    /// <summary>
    /// Starts <c>apt-get install --only-upgrade</c> of <paramref name="packages"/> (after refreshing the lists) as the transient systemd
    /// unit <paramref name="unit"/> on the host. Returns at once; the unit writes its log and exit code to <see cref="LogDirectory"/>.
    /// Existing configuration files are kept (<c>--force-confold</c>), nothing asks questions (<c>noninteractive</c>).
    /// </summary>
    public static string StartUpgrade(IReadOnlyCollection<string> packages, string unit, string helperImage, string dockerHead = "docker")
    {
        EnsureImage(helperImage);
        EnsureUnit(unit);
        if (packages.Count == 0)
        {
            throw new ArgumentException("No packages to upgrade.", nameof(packages));
        }

        foreach (var package in packages)
        {
            if (!IsValidPackageName(package))
            {
                throw new ArgumentException($"Invalid package name: '{package}'.", nameof(packages));
            }
        }

        var log = $"{LogDirectory}/{unit}";
        var script =
            $"mkdir -p {LogDirectory}; " +
            "{ export DEBIAN_FRONTEND=noninteractive LC_ALL=C; apt-get update -q && " +
            "apt-get install -y -q --only-upgrade -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold " +
            string.Join(' ', packages) + $"; }} >{log}.log 2>&1; echo $? >{log}.exit";

        return VolumeCommands.PathPrefix + dockerHead
               + " run --rm --log-driver none --privileged --pid=host "
               + VolumeFileCommands.ShellQuote(helperImage)
               + $" nsenter -t 1 -m -u -i -n -- systemd-run --quiet --collect --unit {unit} /bin/sh -c "
               + VolumeFileCommands.ShellQuote(script);
    }

    /// <summary>The exit code of the upgrade (<c>exit|&lt;n&gt;</c>, missing while it runs) and the end of its log.</summary>
    public static string ReadUpgrade(string unit, string helperImage, string dockerHead = "docker", int tailLines = 200)
    {
        EnsureImage(helperImage);
        EnsureUnit(unit);
        var log = $"/host{LogDirectory}/{unit}";
        var inner = $"[ -f {log}.exit ] && echo \"exit|$(cat {log}.exit)\"; [ -f {log}.log ] && tail -n {Math.Clamp(tailLines, 1, 5000)} {log}.log; exit 0";
        return VolumeCommands.PathPrefix + dockerHead
               + " run --rm --log-driver none -v /var/log:/host/var/log:ro "
               + VolumeFileCommands.ShellQuote(helperImage) + " sh -c " + VolumeFileCommands.ShellQuote(inner);
    }

    /// <summary>Reads <see cref="ReadUpgrade"/>: null exit code while the upgrade still runs.</summary>
    public static (int? ExitCode, string Log) ParseUpgrade(string output)
    {
        int? exit = null;
        var log = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (exit is null && line.StartsWith("exit|", StringComparison.Ordinal)
                && int.TryParse(line[5..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                exit = code;
            }
            else
            {
                log.Add(line);
            }
        }

        return (exit, string.Join('\n', log).TrimEnd());
    }

    private static void EnsureImage(string helperImage)
    {
        if (!VolumeCommands.IsValidImage(helperImage))
        {
            throw new ArgumentException($"Invalid image: '{helperImage}'.", nameof(helperImage));
        }
    }

    private static void EnsureUnit(string unit)
    {
        if (!IsValidUnitName(unit))
        {
            throw new ArgumentException($"Invalid unit name: '{unit}'.", nameof(unit));
        }
    }
}
