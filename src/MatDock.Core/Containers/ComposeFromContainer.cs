using YamlDotNet.Serialization;

namespace MatDock.Core.Containers;

/// <summary>
/// A compose file for a running container, from what makes it differ from its image (<see cref="ContainerRunSpecs.FromInspect"/>): image, name, restart
/// policy, command, environment, ports, volumes, network, labels, limits, capabilities, devices, logging and health check. A named volume and a network
/// of its own are declared <c>external</c>, so the stack uses the data and the network that are there. A variable with a secret's name becomes
/// <c>${NAME}</c>; its value is answered apart (for the stack's secrets), never written into the file.
/// </summary>
public static class ComposeFromContainer
{
    private static readonly ISerializer Yaml = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>Networks that are Docker's own, not something to declare.</summary>
    private static readonly HashSet<string> BuiltInNetworks = new(StringComparer.Ordinal) { "bridge", "default", "none" };

    public sealed record Result(string Yaml, IReadOnlyDictionary<string, string> Secrets);

    public static Result Render(ContainerRunSpec spec, string service)
    {
        var secrets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var svc = new Dictionary<string, object?>(StringComparer.Ordinal) { ["image"] = spec.Image };
        if (!string.IsNullOrWhiteSpace(spec.Name))
        {
            svc["container_name"] = spec.Name;
        }

        if (spec.Restart is { Length: > 0 } restart && restart != "no")
        {
            svc["restart"] = restart;
        }

        if (!string.IsNullOrWhiteSpace(spec.Entrypoint))
        {
            svc["entrypoint"] = spec.Entrypoint;
        }

        if (spec.Command.Count > 0)
        {
            svc["command"] = spec.Command.ToList();
        }

        if (spec.Env.Count > 0)
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in spec.Env)
            {
                var eq = entry.IndexOf('=');
                var name = eq < 0 ? entry : entry[..eq];
                var value = eq < 0 ? string.Empty : entry[(eq + 1)..];
                if (ContainerInspect.IsSecretName(name) && value.Length > 0 && Stacks.StackSecrets.Problem(name, value) is null)
                {
                    secrets[name] = value;
                    env[name] = "${" + name + "}";
                }
                else
                {
                    // compose reads $ as the start of a variable: a literal one is written $$
                    env[name] = value.Replace("$", "$$", StringComparison.Ordinal);
                }
            }

            svc["environment"] = env;
        }

        if (spec.Ports.Count > 0)
        {
            svc["ports"] = spec.Ports.ToList();
        }

        var volumes = new SortedSet<string>(StringComparer.Ordinal);
        if (spec.Volumes.Count > 0)
        {
            svc["volumes"] = spec.Volumes.ToList();
            foreach (var volume in spec.Volumes)
            {
                var source = volume.Split(':')[0];
                if (source.Length > 0 && !source.StartsWith('/') && !source.StartsWith('.') && !source.StartsWith('~'))
                {
                    volumes.Add(source);
                }
            }
        }

        string? network = null;
        if (spec.Network is { Length: > 0 } net)
        {
            if (net is "host" or "none" || net.StartsWith("container:", StringComparison.Ordinal))
            {
                svc["network_mode"] = net;
            }
            else if (!BuiltInNetworks.Contains(net))
            {
                network = net;
                var options = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(spec.Ip))
                {
                    options["ipv4_address"] = spec.Ip;
                }

                if (spec.NetworkAliases.Count > 0)
                {
                    options["aliases"] = spec.NetworkAliases.ToList();
                }

                svc["networks"] = new Dictionary<string, object?>(StringComparer.Ordinal) { [net] = options.Count == 0 ? null : options };
            }
        }

        if (spec.Labels.Count > 0)
        {
            svc["labels"] = spec.Labels.Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty, StringComparer.Ordinal);
        }

        Put(svc, "hostname", spec.Hostname);
        Put(svc, "user", spec.User);
        Put(svc, "working_dir", spec.WorkDir);
        if (spec.Privileged)
        {
            svc["privileged"] = true;
        }

        PutList(svc, "cap_add", spec.CapAdd);
        PutList(svc, "cap_drop", spec.CapDrop);
        PutList(svc, "devices", spec.Devices);
        PutList(svc, "dns", spec.Dns);
        PutList(svc, "extra_hosts", spec.ExtraHosts);
        Put(svc, "mem_limit", spec.Memory);
        Put(svc, "cpus", spec.Cpus);

        if (!string.IsNullOrWhiteSpace(spec.LogDriver) || spec.LogMaxSize is not null || spec.LogMaxFile is not null)
        {
            var logging = new Dictionary<string, object?>(StringComparer.Ordinal) { ["driver"] = spec.LogDriver ?? "json-file" };
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            if (spec.LogMaxSize is { } size) { options["max-size"] = size; }
            if (spec.LogMaxFile is { } files) { options["max-file"] = files.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            if (options.Count > 0) { logging["options"] = options; }
            svc["logging"] = logging;
        }

        if (!string.IsNullOrWhiteSpace(spec.HealthCmd))
        {
            var health = new Dictionary<string, object?>(StringComparer.Ordinal) { ["test"] = new List<string> { "CMD-SHELL", spec.HealthCmd } };
            Put(health, "interval", spec.HealthInterval);
            if (spec.HealthRetries is { } retries) { health["retries"] = retries; }
            svc["healthcheck"] = health;
        }

        if (!string.IsNullOrWhiteSpace(spec.Gpus))
        {
            svc["deploy"] = new Dictionary<string, object?>
            {
                ["resources"] = new Dictionary<string, object?>
                {
                    ["reservations"] = new Dictionary<string, object?>
                    {
                        ["devices"] = new List<object> { new Dictionary<string, object?> { ["driver"] = "nvidia", ["count"] = spec.Gpus == "all" ? "all" : spec.Gpus, ["capabilities"] = new List<string> { "gpu" } } },
                    },
                },
            };
        }

        var root = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["services"] = new Dictionary<string, object?>(StringComparer.Ordinal) { [service] = svc },
        };
        if (volumes.Count > 0)
        {
            root["volumes"] = volumes.ToDictionary(v => v, _ => (object?)new Dictionary<string, object> { ["external"] = true }, StringComparer.Ordinal);
        }

        if (network is not null)
        {
            root["networks"] = new Dictionary<string, object?> { [network] = new Dictionary<string, object> { ["external"] = true } };
        }

        return new Result(Yaml.Serialize(root), secrets);
    }

    private static void Put(Dictionary<string, object?> map, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            map[key] = value;
        }
    }

    private static void PutList(Dictionary<string, object?> map, string key, List<string> values)
    {
        if (values.Count > 0)
        {
            map[key] = values.ToList();
        }
    }
}
