using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatDock.Core.Containers;

/// <summary>One variable of a container's environment; the value of a secret-looking name is never handed out (<see cref="Masked"/>).</summary>
public sealed record ContainerEnvVar(string Name, string Value, bool Masked);

/// <summary>A network a container is attached to, with its addresses.</summary>
public sealed record ContainerNetwork(string Name, string? IpAddress, string? Ipv6Address, string? Gateway, string? MacAddress, IReadOnlyList<string> Aliases,
    string? FixedIp = null);

/// <summary>A mount of a container as <c>docker inspect</c> lists it.</summary>
public sealed record ContainerInspectMount(string Type, string? Name, string? Source, string Destination, bool ReadWrite);

/// <summary>One run of a container's health check.</summary>
public sealed record ContainerHealthRun(string? Start, int ExitCode, string Output);

/// <summary>A process inside a running container (<c>docker top</c>): the columns as Docker names them.</summary>
public sealed record ContainerProcessTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// What <c>docker inspect</c> says about a container, read into what a person wants to see: state and times, health with its log, restart policy,
/// command, environment (secrets masked), labels, networks, limits. <see cref="RawJson"/> is the whole answer with the same values masked.
/// </summary>
public sealed partial class ContainerInspect
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string? ImageId { get; init; }
    public string? Created { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool Running { get; init; }
    public bool Paused { get; init; }
    public bool Restarting { get; init; }
    public bool OomKilled { get; init; }
    public int ExitCode { get; init; }
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
    public string? StateError { get; init; }
    public int RestartCount { get; init; }
    public string RestartPolicy { get; init; } = "no";
    public int RestartMaxRetries { get; init; }
    public string? HealthStatus { get; init; }
    public int HealthFailingStreak { get; init; }
    public IReadOnlyList<ContainerHealthRun> HealthLog { get; init; } = Array.Empty<ContainerHealthRun>();
    public string? HealthTest { get; init; }
    public IReadOnlyList<string> Entrypoint { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Cmd { get; init; } = Array.Empty<string>();
    public string? WorkingDir { get; init; }
    public string? User { get; init; }
    public string? Hostname { get; init; }
    public IReadOnlyList<ContainerEnvVar> Env { get; init; } = Array.Empty<ContainerEnvVar>();
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ContainerNetwork> Networks { get; init; } = Array.Empty<ContainerNetwork>();
    public string? NetworkMode { get; init; }
    public long MemoryBytes { get; init; }
    public double Cpus { get; init; }
    public bool Privileged { get; init; }
    public IReadOnlyList<string> CapAdd { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Devices { get; init; } = Array.Empty<string>();
    public string? LogDriver { get; init; }
    public IReadOnlyDictionary<string, string> LogOptions { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Dns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExtraHosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CapDrop { get; init; } = Array.Empty<string>();

    /// <summary>The published ports as <c>[address:]hostPort:containerPort[/udp]</c>.</summary>
    public IReadOnlyList<string> PortBindings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ContainerInspectMount> Mounts { get; init; } = Array.Empty<ContainerInspectMount>();

    /// <summary>The GPUs asked for: <c>all</c>, a number or <c>device=0,1</c>; null for none.</summary>
    public string? Gpus { get; init; }

    /// <summary>The health check as Docker keeps it (<c>CMD-SHELL</c>, the command …) with its interval and retries.</summary>
    public IReadOnlyList<string> HealthTestRaw { get; init; } = Array.Empty<string>();

    public TimeSpan? HealthInterval { get; init; }

    public int HealthRetries { get; init; }

    /// <summary>The whole inspect answer, indented, with secret-looking environment values masked.</summary>
    public string RawJson { get; init; } = string.Empty;

    /// <summary>The compose project the container belongs to (a stack), or null.</summary>
    public string? Project => Labels.TryGetValue("com.docker.compose.project", out var p) && p.Length > 0 ? p : null;

    public string? Service => Labels.TryGetValue("com.docker.compose.service", out var s) && s.Length > 0 ? s : null;

    /// <summary>What stands in place of a masked value.</summary>
    public const string Mask = "••••••";

    // A name that looks like it holds a secret: PASSWORD, DB_PASS, API_KEY, *_TOKEN, SECRET, PRIVATE_KEY, credentials …
    [GeneratedRegex(@"PASS|PWD|SECRET|TOKEN|KEY|CREDENTIAL|AUTH|PRIVATE|CERT", RegexOptions.IgnoreCase)]
    private static partial Regex SecretNameRegex();

    public static bool IsSecretName(string name) => SecretNameRegex().IsMatch(name);

    /// <summary>Reads the JSON of <c>docker inspect</c> (an array with one object, or the object). Null when it is not a container.</summary>
    /// <param name="json">The answer of <c>docker inspect</c>.</param>
    /// <param name="maskSecrets">False only for MatDock's own use (rebuilding the container): the values then never go to a page.</param>
    public static ContainerInspect? Parse(string json, bool maskSecrets = true)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is JsonArray { Count: > 0 } array)
        {
            root = array[0];
        }

        if (root is not JsonObject obj || obj["Id"] is null)
        {
            return null;
        }

        var config = obj["Config"] as JsonObject;
        var host = obj["HostConfig"] as JsonObject;
        var state = obj["State"] as JsonObject;
        var health = state?["Health"] as JsonObject;

        var env = Strings(config?["Env"])
            .Select(e =>
            {
                var eq = e.IndexOf('=');
                var name = eq < 0 ? e : e[..eq];
                var value = eq < 0 ? string.Empty : e[(eq + 1)..];
                var masked = maskSecrets && IsSecretName(name) && value.Length > 0;
                return new ContainerEnvVar(name, masked ? Mask : value, masked);
            })
            .ToList();

        // The raw view gets the same masking: the inspect answer is mostly what a person copies to someone else.
        if (maskSecrets && config?["Env"] is JsonArray rawEnv)
        {
            for (var i = 0; i < rawEnv.Count; i++)
            {
                var entry = rawEnv[i]?.GetValue<string>() ?? string.Empty;
                var eq = entry.IndexOf('=');
                if (eq > 0 && IsSecretName(entry[..eq]) && eq < entry.Length - 1)
                {
                    rawEnv[i] = entry[..(eq + 1)] + Mask;
                }
            }
        }

        var networks = new List<ContainerNetwork>();
        if (obj["NetworkSettings"]?["Networks"] is JsonObject nets)
        {
            foreach (var (name, value) in nets)
            {
                var fixedIp = Text(value?["IPAMConfig"]?["IPv4Address"]) is { Length: > 0 } v4 ? v4 : Text(value?["IPAMConfig"]?["IPv6Address"]) is { Length: > 0 } v6 ? v6 : null;
                networks.Add(new ContainerNetwork(
                    name,
                    Text(value?["IPAddress"]),
                    Text(value?["GlobalIPv6Address"]),
                    Text(value?["Gateway"]),
                    Text(value?["MacAddress"]),
                    Strings(value?["Aliases"]).ToList(),
                    fixedIp));
            }
        }

        var healthLog = new List<ContainerHealthRun>();
        if (health?["Log"] is JsonArray log)
        {
            foreach (var run in log.OfType<JsonObject>())
            {
                healthLog.Add(new ContainerHealthRun(Text(run["Start"]), Int(run["ExitCode"]), (Text(run["Output"]) ?? string.Empty).Trim()));
            }
        }

        var healthTest = Strings(config?["Healthcheck"]?["Test"]).ToList();

        var ports = new List<string>();
        if (host?["PortBindings"] is JsonObject bindings)
        {
            foreach (var (containerPort, list) in bindings)
            {
                var (number, protocol) = containerPort.Split('/') is [var n, var p] ? (n, p) : (containerPort, "tcp");
                foreach (var binding in (list as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                {
                    var hostIp = Text(binding["HostIp"]);
                    var hostPort = Text(binding["HostPort"]);
                    if (string.IsNullOrEmpty(hostPort) || hostIp == "::")
                    {
                        continue; // no host port (an unpublished one) or the IPv6 twin of the same binding
                    }

                    var address = string.IsNullOrEmpty(hostIp) || hostIp == "0.0.0.0" ? string.Empty : hostIp + ":";
                    ports.Add($"{address}{hostPort}:{number}{(protocol == "tcp" ? string.Empty : "/" + protocol)}");
                }
            }
        }

        var mounts = (obj["Mounts"] as JsonArray)?.OfType<JsonObject>()
            .Select(m => new ContainerInspectMount(Text(m["Type"]) ?? string.Empty, Text(m["Name"]), Text(m["Source"]), Text(m["Destination"]) ?? string.Empty, m["RW"] is null || Bool(m["RW"])))
            .ToList() ?? new List<ContainerInspectMount>();

        string? gpus = null;
        foreach (var request in (host?["DeviceRequests"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (!Strings(request["Capabilities"]?[0]).Contains("gpu") && request["Capabilities"] is JsonArray { Count: > 0 })
            {
                continue;
            }

            var ids = Strings(request["DeviceIDs"]).ToList();
            var count = Int(request["Count"]);
            gpus = ids.Count > 0 ? "device=" + string.Join(',', ids) : count == -1 ? "all" : count > 0 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : gpus;
        }

        return new ContainerInspect
        {
            Id = Text(obj["Id"]) ?? string.Empty,
            Name = (Text(obj["Name"]) ?? string.Empty).TrimStart('/'),
            Image = Text(config?["Image"]) ?? string.Empty,
            ImageId = Text(obj["Image"]),
            Created = Text(obj["Created"]),
            Status = Text(state?["Status"]) ?? string.Empty,
            Running = Bool(state?["Running"]),
            Paused = Bool(state?["Paused"]),
            Restarting = Bool(state?["Restarting"]),
            OomKilled = Bool(state?["OOMKilled"]),
            ExitCode = Int(state?["ExitCode"]),
            StartedAt = Time(Text(state?["StartedAt"])),
            FinishedAt = Time(Text(state?["FinishedAt"])),
            StateError = Text(state?["Error"]) is { Length: > 0 } err ? err : null,
            RestartCount = Int(obj["RestartCount"]),
            RestartPolicy = Text(host?["RestartPolicy"]?["Name"]) is { Length: > 0 } policy ? policy : "no",
            RestartMaxRetries = Int(host?["RestartPolicy"]?["MaximumRetryCount"]),
            HealthStatus = Text(health?["Status"]),
            HealthFailingStreak = Int(health?["FailingStreak"]),
            HealthLog = healthLog,
            HealthTest = healthTest.Count == 0 || healthTest[0] == "NONE" ? null : string.Join(' ', healthTest.Skip(healthTest[0] is "CMD" or "CMD-SHELL" ? 1 : 0)),
            Entrypoint = Strings(config?["Entrypoint"]).ToList(),
            Cmd = Strings(config?["Cmd"]).ToList(),
            WorkingDir = Text(config?["WorkingDir"]) is { Length: > 0 } wd ? wd : null,
            User = Text(config?["User"]) is { Length: > 0 } u ? u : null,
            Hostname = Text(config?["Hostname"]),
            Env = env,
            Labels = (config?["Labels"] as JsonObject)?
                .Where(l => l.Value is JsonValue)
                .OrderBy(l => l.Key, StringComparer.Ordinal)
                .ToDictionary(l => l.Key, l => Text(l.Value) ?? string.Empty) ?? new Dictionary<string, string>(),
            Networks = networks,
            NetworkMode = Text(host?["NetworkMode"]),
            MemoryBytes = Long(host?["Memory"]),
            Cpus = Long(host?["NanoCpus"]) / 1_000_000_000d,
            Privileged = Bool(host?["Privileged"]),
            CapAdd = Strings(host?["CapAdd"]).ToList(),
            Devices = (host?["Devices"] as JsonArray)?.OfType<JsonObject>()
                .Select(d => $"{Text(d["PathOnHost"])}:{Text(d["PathInContainer"])}").ToList() ?? new List<string>(),
            LogDriver = Text(host?["LogConfig"]?["Type"]),
            LogOptions = (host?["LogConfig"]?["Config"] as JsonObject)?
                .ToDictionary(l => l.Key, l => Text(l.Value) ?? string.Empty) ?? new Dictionary<string, string>(),
            Dns = Strings(host?["Dns"]).ToList(),
            ExtraHosts = Strings(host?["ExtraHosts"]).ToList(),
            CapDrop = Strings(host?["CapDrop"]).ToList(),
            PortBindings = ports,
            Mounts = mounts,
            Gpus = gpus,
            HealthTestRaw = healthTest,
            HealthInterval = Long(config?["Healthcheck"]?["Interval"]) is > 0 and var interval ? TimeSpan.FromTicks(interval / 100) : null,
            HealthRetries = Int(config?["Healthcheck"]?["Retries"]),
            RawJson = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        };
    }

    /// <summary>Reads <c>docker top</c>: a header line and the processes; the last column (the command) keeps its spaces.</summary>
    public static ContainerProcessTable ParseTop(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0)
        {
            return new ContainerProcessTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        }

        var columns = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rows = lines.Skip(1)
            .Select(line =>
            {
                var parts = line.Split(' ', columns.Length, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return (IReadOnlyList<string>)parts;
            })
            .ToList();
        return new ContainerProcessTable(columns, rows);
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToString();

    private static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    private static IEnumerable<string> Strings(JsonNode? node)
        => node is JsonArray a ? a.Select(Text).Where(s => s is not null).Cast<string>() : Enumerable.Empty<string>();

    /// <summary>Docker writes "0001-01-01T00:00:00Z" for a time that never was.</summary>
    private static string? Time(string? value) => string.IsNullOrEmpty(value) || value.StartsWith("0001-", StringComparison.Ordinal) ? null : value;
}
