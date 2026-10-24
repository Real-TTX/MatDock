using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatDock.Core.Containers;

/// <summary>What an image brings by itself (<c>docker image inspect … .Config</c>): a container only differs from it where it was told to.</summary>
public sealed record ImageConfig(
    IReadOnlyList<string> Env,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Cmd,
    IReadOnlyList<string> Entrypoint,
    string? WorkingDir,
    string? User,
    IReadOnlyList<string> HealthTest)
{
    public static readonly ImageConfig Empty = new(Array.Empty<string>(), new Dictionary<string, string>(), Array.Empty<string>(), Array.Empty<string>(), null, null, Array.Empty<string>());

    /// <summary>Reads <c>docker image inspect --format '{{json .Config}}'</c>; <see cref="Empty"/> for anything else.</summary>
    public static ImageConfig Parse(string? json)
    {
        JsonObject? config;
        try
        {
            config = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            config = null;
        }

        if (config is null)
        {
            return Empty;
        }

        static IReadOnlyList<string> Strings(JsonNode? node)
            => node is JsonArray a ? a.Select(x => x?.ToString() ?? string.Empty).ToList() : Array.Empty<string>();

        return new ImageConfig(
            Strings(config["Env"]),
            (config["Labels"] as JsonObject)?.ToDictionary(l => l.Key, l => l.Value?.ToString() ?? string.Empty) ?? new Dictionary<string, string>(),
            Strings(config["Cmd"]),
            Strings(config["Entrypoint"]),
            config["WorkingDir"]?.ToString() is { Length: > 0 } wd ? wd : null,
            config["User"]?.ToString() is { Length: > 0 } user ? user : null,
            Strings(config["Healthcheck"]?["Test"]));
    }
}

/// <summary>Turns a container that exists back into a <see cref="ContainerRunSpec"/>: to duplicate it, or to recreate it with the same settings.</summary>
public static class ContainerRunSpecs
{
    /// <summary>
    /// The settings that start the same container again. Only what differs from the image goes in (its environment, labels, command and user are the
    /// image's anyway), the labels of compose are left out (the copy is no part of the stack), and anonymous volumes are taken by their name so their
    /// data stays with the new container.
    /// </summary>
    public static ContainerRunSpec FromInspect(ContainerInspect c, ImageConfig? image = null)
    {
        image ??= ImageConfig.Empty;
        var imageEnv = image.Env.ToHashSet(StringComparer.Ordinal);
        var env = c.Env.Select(e => $"{e.Name}={e.Value}").Where(e => !imageEnv.Contains(e)).ToList();

        var labels = c.Labels
            .Where(l => !l.Key.StartsWith("com.docker.compose.", StringComparison.Ordinal))
            .Where(l => !(image.Labels.TryGetValue(l.Key, out var v) && v == l.Value))
            .Select(l => $"{l.Key}={l.Value}")
            .ToList();

        var volumes = c.Mounts
            .Where(m => m.Destination.StartsWith('/'))
            .Select(m => m.Type switch
            {
                "volume" when !string.IsNullOrEmpty(m.Name) => $"{m.Name}:{m.Destination}{(m.ReadWrite ? string.Empty : ":ro")}",
                "bind" when m.Source?.StartsWith('/') == true => $"{m.Source}:{m.Destination}{(m.ReadWrite ? string.Empty : ":ro")}",
                _ => null,
            })
            .Where(v => v is not null)
            .Cast<string>()
            .ToList();

        // The network: a user-defined one by name; the default bridge needs nothing.
        var network = c.NetworkMode is { Length: > 0 } mode && mode != "default" && mode != "bridge" && !mode.StartsWith("container:", StringComparison.Ordinal) ? mode : null;
        var attached = network is null ? null : c.Networks.FirstOrDefault(n => n.Name == network);
        var shortId = c.Id.Length >= 12 ? c.Id[..12] : c.Id;
        var aliases = attached?.Aliases.Where(a => a != c.Name && a != shortId && a != c.Hostname).ToList() ?? new List<string>();

        // Entrypoint and command only when they differ from the image; docker run takes one program for --entrypoint, so the rest goes in front of the command.
        var entrypointChanged = !c.Entrypoint.SequenceEqual(image.Entrypoint);
        var cmdChanged = !c.Cmd.SequenceEqual(image.Cmd);
        var command = new List<string>();
        string? entrypoint = null;
        if (entrypointChanged && c.Entrypoint.Count > 0)
        {
            entrypoint = c.Entrypoint[0];
            command.AddRange(c.Entrypoint.Skip(1));
        }

        if (entrypointChanged || cmdChanged)
        {
            command.AddRange(c.Cmd);
        }

        var healthChanged = c.HealthTestRaw.Count > 0 && !c.HealthTestRaw.SequenceEqual(image.HealthTest) && c.HealthTestRaw[0] is "CMD-SHELL" or "CMD";

        return new ContainerRunSpec
        {
            Image = c.Image,
            Name = c.Name,
            Ports = c.PortBindings.ToList(),
            Env = env,
            Labels = labels,
            Volumes = volumes,
            Restart = ContainerRunCommands.RestartPolicies.Contains(c.RestartPolicy) ? c.RestartPolicy : "no",
            Network = network,
            Ip = attached?.FixedIp,
            NetworkAliases = aliases,
            Entrypoint = entrypoint,
            Command = command,
            Memory = c.MemoryBytes > 0 ? (c.MemoryBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + "m" : null,
            Cpus = c.Cpus > 0 ? c.Cpus.ToString("0.##", CultureInfo.InvariantCulture) : null,
            Hostname = c.Hostname is { Length: > 0 } h && h != shortId ? h : null,
            User = c.User != image.User ? c.User : null,
            WorkDir = c.WorkingDir != image.WorkingDir ? c.WorkingDir : null,
            Privileged = c.Privileged,
            CapAdd = c.CapAdd.ToList(),
            CapDrop = c.CapDrop.ToList(),
            Devices = c.Devices.Where(d => d.StartsWith("/dev/", StringComparison.Ordinal)).ToList(),
            Gpus = c.Gpus,
            LogDriver = c.LogOptions.Count > 0 || (c.LogDriver is { Length: > 0 } driver && driver != "json-file") ? c.LogDriver : null,
            LogMaxSize = c.LogOptions.TryGetValue("max-size", out var size) ? size : null,
            LogMaxFile = c.LogOptions.TryGetValue("max-file", out var files) && int.TryParse(files, out var n) ? n : null,
            Dns = c.Dns.ToList(),
            ExtraHosts = c.ExtraHosts.ToList(),
            HealthCmd = healthChanged ? string.Join(' ', c.HealthTestRaw.Skip(1)) : null,
            HealthInterval = healthChanged && c.HealthInterval is { } interval ? $"{(int)interval.TotalSeconds}s" : null,
            HealthRetries = healthChanged && c.HealthRetries > 0 ? c.HealthRetries : null,
        };
    }
}
