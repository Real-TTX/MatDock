using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MatDock.Core.Stacks;

public enum LintSeverity
{
    Error = 0,
    Warning = 1,
    Suggestion = 2,
}

/// <summary>What the check found: a rule (<see cref="Code"/>), how bad, where (1-based line) and, when MatDock can repair it, the fix to apply
/// (<see cref="ComposeLint.ApplyFix"/>) and the words of its button.</summary>
public sealed record LintFinding(string Code, LintSeverity Severity, string Message, int Line, string? Service = null, string? Fix = null, string? FixLabel = null);

/// <summary>A host port another stack or container of the environment publishes (for <c>CROSS_STACK_PORT_COLLISION</c>).</summary>
public sealed record HostPortUse(int Port, string Protocol, string Owner);

/// <summary>
/// The rules of the stack editor's check, advisory: they never stop a save or a deploy (compose itself does that, and the quick
/// <see cref="ComposeCheck"/>). Errors are what compose will refuse or what cannot work, warnings what is risky, suggestions what is better
/// practice. Some come with a fix that edits the text in place (<see cref="ApplyFix"/>): remove the obsolete <c>version</c>, add a restart policy,
/// bind a database port to 127.0.0.1, correct a misspelt key.
/// </summary>
public static partial class ComposeLint
{
    /// <summary>The keys of a service in the compose specification.</summary>
    private static readonly HashSet<string> ServiceKeys = new(StringComparer.Ordinal)
    {
        "annotations", "attach", "blkio_config", "build", "cap_add", "cap_drop", "cgroup", "cgroup_parent", "command", "configs", "container_name",
        "cpu_count", "cpu_percent", "cpu_period", "cpu_quota", "cpu_rt_period", "cpu_rt_runtime", "cpu_shares", "cpus", "cpuset", "credential_spec",
        "depends_on", "deploy", "develop", "device_cgroup_rules", "devices", "dns", "dns_opt", "dns_search", "domainname", "driver_opts", "entrypoint",
        "env_file", "environment", "expose", "extends", "external_links", "extra_hosts", "gpus", "group_add", "healthcheck", "hostname", "image", "init",
        "ipc", "isolation", "labels", "label_file", "links", "logging", "mac_address", "mem_limit", "mem_reservation", "mem_swappiness", "memswap_limit",
        "models", "network_mode", "networks", "oom_kill_disable", "oom_score_adj", "pid", "pids_limit", "platform", "ports", "post_start", "pre_stop",
        "privileged", "profiles", "provider", "pull_policy", "read_only", "restart", "runtime", "scale", "secrets", "security_opt", "shm_size",
        "stdin_open", "stop_grace_period", "stop_signal", "storage_opt", "sysctls", "tmpfs", "tty", "ulimits", "use_api_socket", "user", "userns_mode",
        "uts", "volumes", "volumes_from", "working_dir",
    };

    /// <summary>The top-level keys of a compose file.</summary>
    private static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "version", "name", "services", "networks", "volumes", "configs", "secrets", "include", "models",
    };

    /// <summary>Container ports of databases and caches that should not listen on every interface of the host.</summary>
    private static readonly HashSet<int> DatabasePorts = [5432, 3306, 6379, 27017, 9200, 5984, 11211, 1433, 9042, 8086];

    [GeneratedRegex(@"(PASSWORD|PASSWD|SECRET|TOKEN|API_?KEY|PRIVATE_?KEY|ACCESS_?KEY)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretNameRegex();

    /// <summary>
    /// Checks a compose file. <paramref name="defined"/> are the variables with a value (the <c>.env</c>, the secrets; null = do not check variables,
    /// e.g. a Git stack whose <c>.env</c> lives in the repository); <paramref name="otherPorts"/> the host ports the rest of the environment publishes.
    /// </summary>
    public static IReadOnlyList<LintFinding> Check(string? yaml, IEnumerable<string>? defined = null, IReadOnlyCollection<HostPortUse>? otherPorts = null)
    {
        var findings = new List<LintFinding>();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            findings.Add(new LintFinding("EMPTY", LintSeverity.Error, "The compose file is empty.", 1));
            return findings;
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            var colon = message.IndexOf("): ", StringComparison.Ordinal);
            findings.Add(new LintFinding("YAML_SYNTAX", LintSeverity.Error, colon >= 0 ? message[(colon + 3)..] : message, (int)Math.Max(1, ex.Start.Line)));
            return findings;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            findings.Add(new LintFinding("NOT_A_MAP", LintSeverity.Error, "The compose file must be a map with 'services:'.", 1));
            return findings;
        }

        foreach (var (key, _) in root.Children)
        {
            var name = Scalar(key);
            if (name is null || name.StartsWith("x-", StringComparison.Ordinal) || TopKeys.Contains(name))
            {
                continue;
            }

            findings.Add(Unknown(name, key, TopKeys, null, "top-level key"));
        }

        if (Child(root, "version") is { } version)
        {
            findings.Add(new LintFinding("OBSOLETE_VERSION_KEY", LintSeverity.Suggestion, "'version' is obsolete: compose ignores it and warns.",
                Line(KeyOf(root, "version") ?? version), Fix: "remove-version", FixLabel: "Remove"));
        }

        var declaredVolumes = Keys(Child(root, "volumes"));
        var declaredNetworks = Keys(Child(root, "networks"));
        var usedVolumes = new HashSet<string>(StringComparer.Ordinal);
        var usedNetworks = new HashSet<string>(StringComparer.Ordinal);
        var ports = new List<(string Ip, int Port, string Protocol, int Line, string Service)>();

        if (Child(root, "services") is not YamlMappingNode services)
        {
            if (Child(root, "include") is null)
            {
                findings.Add(new LintFinding("NO_SERVICES", LintSeverity.Error, "The compose file has no 'services:' map.", 1));
            }

            return Sorted(findings);
        }

        var serviceNames = services.Children.Keys.Select(Scalar).Where(n => n is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (key, value) in services.Children)
        {
            var name = Scalar(key) ?? "?";
            var line = Line(key);
            if (value is not YamlMappingNode service)
            {
                findings.Add(new LintFinding("SERVICE_NOT_A_MAP", LintSeverity.Error, $"Service '{name}' is not a map.", line, name));
                continue;
            }

            CheckService(name, key, service, serviceNames, declaredVolumes, declaredNetworks, usedVolumes, usedNetworks, ports, findings);
        }

        // Host ports: twice in this file, or taken by another stack or container of the environment.
        foreach (var group in ports.GroupBy(p => (p.Port, p.Protocol)))
        {
            var uses = group.ToList();
            for (var i = 1; i < uses.Count; i++)
            {
                var clash = uses.Take(i).FirstOrDefault(u => u.Ip == uses[i].Ip || u.Ip.Length == 0 || uses[i].Ip.Length == 0);
                if (clash.Service is not null)
                {
                    findings.Add(new LintFinding("DUPLICATE_HOST_PORT", LintSeverity.Error,
                        $"Host port {group.Key.Port}/{group.Key.Protocol} is published twice (also by '{clash.Service}', line {clash.Line}).", uses[i].Line, uses[i].Service));
                }
            }

            if (otherPorts?.FirstOrDefault(o => o.Port == group.Key.Port && string.Equals(o.Protocol, group.Key.Protocol, StringComparison.OrdinalIgnoreCase)) is { } other)
            {
                findings.Add(new LintFinding("CROSS_STACK_PORT_COLLISION", LintSeverity.Warning,
                    $"Host port {group.Key.Port}/{group.Key.Protocol} is already published by {other.Owner} on this environment.", uses[0].Line, uses[0].Service));
            }
        }

        foreach (var volume in declaredVolumes.Where(v => !usedVolumes.Contains(v)))
        {
            findings.Add(new LintFinding("UNUSED_VOLUME", LintSeverity.Suggestion, $"Volume '{volume}' is declared but no service uses it.",
                Line(KeyOf((YamlMappingNode)Child(root, "volumes")!, volume)!)));
        }

        foreach (var network in declaredNetworks.Where(n => n != "default" && !usedNetworks.Contains(n)))
        {
            findings.Add(new LintFinding("UNUSED_NETWORK", LintSeverity.Suggestion, $"Network '{network}' is declared but no service uses it.",
                Line(KeyOf((YamlMappingNode)Child(root, "networks")!, network)!)));
        }

        if (defined is not null)
        {
            foreach (var use in StackVariables.Missing(yaml, defined))
            {
                findings.Add(use.Required
                    ? new LintFinding("MISSING_VARIABLE", LintSeverity.Error, $"${{{use.Name}}} is required but has no value (add it to the .env or the secrets).", use.Line)
                    : new LintFinding("MISSING_VARIABLE", LintSeverity.Warning, $"${{{use.Name}}} has no value: compose uses an empty string.", use.Line));
            }
        }

        return Sorted(findings);
    }

    private static void CheckService(string name, YamlNode key, YamlMappingNode service, HashSet<string> serviceNames, HashSet<string> declaredVolumes,
        HashSet<string> declaredNetworks, HashSet<string> usedVolumes, HashSet<string> usedNetworks,
        List<(string Ip, int Port, string Protocol, int Line, string Service)> ports, List<LintFinding> findings)
    {
        var line = Line(key);
        foreach (var (childKey, _) in service.Children)
        {
            var child = Scalar(childKey);
            if (child is not null && !child.StartsWith("x-", StringComparison.Ordinal) && !ServiceKeys.Contains(child))
            {
                findings.Add(Unknown(child, childKey, ServiceKeys, name, $"key of service '{name}'"));
            }
        }

        var image = Scalar(Child(service, "image"));
        var hasBuild = Child(service, "build") is not null;
        if (image is null && !hasBuild && Child(service, "extends") is null)
        {
            findings.Add(new LintFinding("MISSING_IMAGE", LintSeverity.Error, $"Service '{name}' needs an 'image' or a 'build'.", line, name));
        }

        if (image is not null && !hasBuild && !image.Contains("${", StringComparison.Ordinal) && !image.Contains('@'))
        {
            var lastSlash = image.LastIndexOf('/');
            var tag = image.IndexOf(':', lastSlash + 1) is var colon and >= 0 ? image[(colon + 1)..] : null;
            if (tag is null || tag == "latest")
            {
                findings.Add(new LintFinding("LATEST_TAG", LintSeverity.Suggestion,
                    $"'{image}' follows {(tag is null ? "latest (no tag)" : "latest")}: a version tag makes updates deliberate.", Line(Child(service, "image")!), name));
            }
        }

        var deploy = Child(service, "deploy") as YamlMappingNode;
        if (Child(service, "restart") is null && (deploy is null || Child(deploy, "restart_policy") is null))
        {
            findings.Add(new LintFinding("MISSING_RESTART_POLICY", LintSeverity.Suggestion,
                $"Service '{name}' has no restart policy: it stays down after a crash or a reboot.", line, name, $"restart:{name}", "Add restart: unless-stopped"));
        }

        if (Child(service, "healthcheck") is null)
        {
            findings.Add(new LintFinding("MISSING_HEALTHCHECK", LintSeverity.Suggestion,
                $"Service '{name}' has no health check here (the image may bring one): updates with a health check can roll back.", line, name));
        }

        if (Scalar(Child(service, "privileged")) is "true" or "True")
        {
            findings.Add(new LintFinding("PRIVILEGED_CONTAINER", LintSeverity.Warning,
                $"Service '{name}' runs privileged: it can do almost anything on the host.", Line(Child(service, "privileged")!), name));
        }

        if (Scalar(Child(service, "network_mode")) == "host")
        {
            findings.Add(new LintFinding("HOST_NETWORK_MODE", LintSeverity.Warning,
                $"Service '{name}' uses the host's network: its ports are not isolated and 'ports' is ignored.", Line(Child(service, "network_mode")!), name));
        }

        CheckVolumes(name, Child(service, "volumes"), declaredVolumes, usedVolumes, findings);
        CheckPorts(name, Child(service, "ports"), ports, findings);

        foreach (var (dependency, node) in Entries(Child(service, "depends_on")))
        {
            if (!serviceNames.Contains(dependency))
            {
                findings.Add(new LintFinding("DEPENDS_ON_UNDEFINED", LintSeverity.Error, $"Service '{name}' depends on '{dependency}', which is not a service of this file.", Line(node), name));
            }
        }

        foreach (var (network, node) in Entries(Child(service, "networks")))
        {
            usedNetworks.Add(network);
            if (network != "default" && !declaredNetworks.Contains(network))
            {
                findings.Add(new LintFinding("UNDEFINED_NETWORK", LintSeverity.Error, $"Network '{network}' is not declared under 'networks:'.", Line(node), name));
            }
        }

        foreach (var (variable, value, node) in Environment(Child(service, "environment")))
        {
            if (SecretNameRegex().IsMatch(variable) && !string.IsNullOrEmpty(value) && !value.Contains('$'))
            {
                findings.Add(new LintFinding("SECRET_IN_ENVIRONMENT", LintSeverity.Warning,
                    $"{variable} is written into the file: put it into a secret and write ${{{variable}}} here.", Line(node), name));
            }
        }
    }

    private static void CheckVolumes(string service, YamlNode? volumes, HashSet<string> declared, HashSet<string> used, List<LintFinding> findings)
    {
        if (volumes is not YamlSequenceNode list)
        {
            return;
        }

        foreach (var item in list.Children)
        {
            string? source;
            var isVolume = false;
            if (item is YamlScalarNode scalar)
            {
                var parts = (scalar.Value ?? string.Empty).Split(':');
                source = parts.Length > 1 ? parts[0] : null;
                isVolume = source is { Length: > 0 } && !source.StartsWith('/') && !source.StartsWith('.') && !source.StartsWith('~') && !source.Contains('$')
                           && !(source.Length >= 2 && source[1] == ':');
            }
            else if (item is YamlMappingNode map)
            {
                source = Scalar(Child(map, "source"));
                isVolume = Scalar(Child(map, "type")) is null or "volume" && source is { Length: > 0 } && !source.Contains('/') && !source.Contains('$');
            }
            else
            {
                continue;
            }

            if (source is not null && (source == "/var/run/docker.sock" || source.EndsWith("/docker.sock", StringComparison.Ordinal)))
            {
                findings.Add(new LintFinding("DOCKER_SOCKET_MOUNT", LintSeverity.Warning,
                    $"Service '{service}' mounts the Docker socket: that is root on the host.", Line(item), service));
            }

            if (isVolume && source is not null)
            {
                used.Add(source);
                if (!declared.Contains(source))
                {
                    findings.Add(new LintFinding("UNDEFINED_VOLUME", LintSeverity.Error, $"Volume '{source}' is not declared under 'volumes:'.", Line(item), service));
                }
            }
        }
    }

    // "8080:80", "127.0.0.1:8080:80/tcp", "80" (no host port), ranges "8000-8001:8000-8001"
    [GeneratedRegex(@"^(?:(?<ip>\[[^\]]+\]|\d{1,3}(?:\.\d{1,3}){3}):)?(?:(?<host>\d+(?:-\d+)?):)?(?<container>\d+(?:-\d+)?)(?:/(?<proto>tcp|udp|sctp))?$")]
    private static partial Regex PortRegex();

    private static void CheckPorts(string service, YamlNode? portsNode, List<(string Ip, int Port, string Protocol, int Line, string Service)> ports, List<LintFinding> findings)
    {
        if (portsNode is not YamlSequenceNode list)
        {
            return;
        }

        foreach (var item in list.Children)
        {
            var line = Line(item);
            string ip;
            string? host;
            string container;
            string protocol;
            if (item is YamlScalarNode scalar)
            {
                var m = PortRegex().Match(scalar.Value ?? string.Empty);
                if (!m.Success)
                {
                    continue; // a variable or something compose will judge
                }

                ip = m.Groups["ip"].Value;
                host = m.Groups["host"].Success ? m.Groups["host"].Value : null;
                container = m.Groups["container"].Value;
                protocol = m.Groups["proto"].Success ? m.Groups["proto"].Value : "tcp";

                var containerPort = int.TryParse(container, out var c) ? c : 0;
                if (host is not null && (ip.Length == 0 || ip == "0.0.0.0") && DatabasePorts.Contains(containerPort))
                {
                    findings.Add(new LintFinding("DB_PORT_ON_ALL_INTERFACES", LintSeverity.Warning,
                        $"Port {host} of service '{service}' (a database) is reachable from every network of the host.", line, service,
                        $"bind-local:{line}", "Bind to 127.0.0.1"));
                }
            }
            else if (item is YamlMappingNode map)
            {
                ip = Scalar(Child(map, "host_ip")) ?? string.Empty;
                host = Scalar(Child(map, "published"));
                container = Scalar(Child(map, "target")) ?? string.Empty;
                protocol = Scalar(Child(map, "protocol")) ?? "tcp";
            }
            else
            {
                continue;
            }

            if (ip == "0.0.0.0")
            {
                ip = string.Empty;
            }

            if (host is null || host.Contains('-') || !int.TryParse(host, out var hostPort))
            {
                continue; // no fixed host port (or a range)
            }

            ports.Add((ip, hostPort, protocol.ToLowerInvariant(), line, service));
        }
    }

    /// <summary>A key that compose does not know, with a fix when it is close to one it knows.</summary>
    private static LintFinding Unknown(string name, YamlNode key, IReadOnlySet<string> known, string? service, string what)
    {
        var best = known.Select(k => (Key: k, Distance: Distance(name.ToLowerInvariant(), k))).Where(k => k.Distance <= 2 && k.Distance < name.Length / 2 + 1)
            .OrderBy(k => k.Distance).FirstOrDefault().Key;
        var line = Line(key);
        return best is null
            ? new LintFinding("UNKNOWN_KEY", LintSeverity.Warning, $"'{name}' is not a {what} compose knows.", line, service)
            : new LintFinding("UNKNOWN_KEY", LintSeverity.Warning, $"'{name}' is not a {what} compose knows. Did you mean '{best}'?", line, service,
                $"rename:{line}:{name}:{best}", $"Rename to {best}");
    }

    // ---- fixes ----

    /// <summary>
    /// The compose file with a fix of <see cref="Check"/> applied, or null when the fix does not fit the text (any more). Fixes edit lines in place, so
    /// comments and the rest of the formatting stay.
    /// </summary>
    public static string? ApplyFix(string yaml, string fix)
    {
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var parts = fix.Split(':');
        switch (parts[0])
        {
            case "remove-version":
            {
                var index = lines.FindIndex(l => VersionLine().IsMatch(l));
                if (index < 0)
                {
                    return null;
                }

                lines.RemoveAt(index);
                if (index < lines.Count && index == 0 && lines[index].Trim().Length == 0)
                {
                    lines.RemoveAt(index);
                }

                break;
            }

            case "restart" when parts.Length == 2:
            {
                var stream = Load(yaml);
                if (stream?.Documents[0].RootNode is not YamlMappingNode root || Child(root, "services") is not YamlMappingNode services
                    || KeyOf(services, parts[1]) is not { } key || Child(services, parts[1]) is not YamlMappingNode service || service.Children.Count == 0)
                {
                    return null;
                }

                if (Child(service, "restart") is not null)
                {
                    return yaml;
                }

                var first = service.Children.Keys.First();
                if (service.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
                {
                    return null;
                }

                var indent = new string(' ', (int)first.Start.Column - 1);
                lines.Insert((int)key.Start.Line, indent + "restart: unless-stopped");
                break;
            }

            case "bind-local" when parts.Length == 2 && int.TryParse(parts[1], out var portLine) && portLine >= 1 && portLine <= lines.Count:
            {
                var text = lines[portLine - 1];
                var m = PortLine().Match(text);
                if (!m.Success)
                {
                    return null;
                }

                lines[portLine - 1] = text[..m.Groups["value"].Index] + "127.0.0.1:" + text[m.Groups["value"].Index..];
                break;
            }

            case "rename" when parts.Length == 4 && int.TryParse(parts[1], out var keyLine) && keyLine >= 1 && keyLine <= lines.Count:
            {
                var text = lines[keyLine - 1];
                var m = Regex.Match(text, @"^(\s*)" + Regex.Escape(parts[2]) + @"(\s*:)");
                if (!m.Success || !ServiceKeys.Contains(parts[3]) && !TopKeys.Contains(parts[3]))
                {
                    return null;
                }

                lines[keyLine - 1] = m.Groups[1].Value + parts[3] + text[(m.Groups[1].Length + parts[2].Length)..];
                break;
            }

            default:
                return null;
        }

        return string.Join(newline, lines);
    }

    [GeneratedRegex(@"^version\s*:")]
    private static partial Regex VersionLine();

    // "  - "5432:5432"" or "  - 5432:5432" (a published port without an address)
    [GeneratedRegex(@"^\s*-\s*[""']?(?<value>\d+(?:-\d+)?:\d+)")]
    private static partial Regex PortLine();

    // ---- helpers ----

    private static YamlStream? Load(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.Count == 0 ? null : stream;
        }
        catch (YamlException)
        {
            return null;
        }
    }

    private static IReadOnlyList<LintFinding> Sorted(List<LintFinding> findings)
        => findings.OrderBy(f => f.Severity).ThenBy(f => f.Line).ThenBy(f => f.Code, StringComparer.Ordinal).ToList();

    private static int Line(YamlNode node) => (int)Math.Max(1, node.Start.Line);

    private static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value;

    private static YamlNode? Child(YamlMappingNode? map, string key)
        => map is not null && map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static YamlNode? Child(YamlNode? node, string key) => Child(node as YamlMappingNode, key);

    private static YamlNode? KeyOf(YamlMappingNode map, string key) => map.Children.Keys.FirstOrDefault(k => Scalar(k) == key);

    private static HashSet<string> Keys(YamlNode? node)
        => node is YamlMappingNode map ? map.Children.Keys.Select(Scalar).Where(k => k is not null).Cast<string>().ToHashSet(StringComparer.Ordinal) : [];

    /// <summary>The names of a list (<c>- a</c>) or the keys of a map (<c>a: {...}</c>), with their nodes.</summary>
    private static IEnumerable<(string Name, YamlNode Node)> Entries(YamlNode? node)
    {
        if (node is YamlSequenceNode list)
        {
            foreach (var item in list.Children)
            {
                if (Scalar(item) is { } name)
                {
                    yield return (name, item);
                }
            }
        }
        else if (node is YamlMappingNode map)
        {
            foreach (var key in map.Children.Keys)
            {
                if (Scalar(key) is { } name)
                {
                    yield return (name, key);
                }
            }
        }
    }

    /// <summary>The variables of <c>environment:</c> as a list (<c>- A=b</c>) or a map (<c>A: b</c>).</summary>
    private static IEnumerable<(string Name, string? Value, YamlNode Node)> Environment(YamlNode? node)
    {
        if (node is YamlSequenceNode list)
        {
            foreach (var item in list.Children)
            {
                if (Scalar(item) is { } entry)
                {
                    var eq = entry.IndexOf('=');
                    yield return eq < 0 ? (entry, null, item) : (entry[..eq], entry[(eq + 1)..], item);
                }
            }
        }
        else if (node is YamlMappingNode map)
        {
            foreach (var (key, value) in map.Children)
            {
                if (Scalar(key) is { } name)
                {
                    yield return (name, Scalar(value), key);
                }
            }
        }
    }

    /// <summary>Levenshtein distance (the keys are short).</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
