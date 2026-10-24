using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatDock.Core.Entities;
using MatDock.Core.Volumes;

namespace MatDock.Core.Docker;

/// <summary>One event of an engine (<c>docker events</c>): what happened (<see cref="Action"/>) to which object (<see cref="Type"/>, <see cref="Name"/>), and its details.</summary>
public sealed record DockerEvent(DateTime TimeUtc, string Type, string Action, string Id, string? Name, IReadOnlyDictionary<string, string> Attributes)
{
    public string? Image => Attributes.TryGetValue("image", out var v) ? v : null;

    /// <summary>The exit code of a <c>die</c>.</summary>
    public string? ExitCode => Attributes.TryGetValue("exitCode", out var v) ? v : null;

    /// <summary>The container a network event is about (connect, disconnect).</summary>
    public string? Container => Attributes.TryGetValue("container", out var v) ? v : null;

    /// <summary>Set by <see cref="DockerEvents.Parse"/> on a <c>die</c> that a <c>kill</c> shortly before explains: the end of a stop, not a crash.</summary>
    public const string StoppedAttribute = "matdock.stopped";

    /// <summary>Something went wrong: a container died with an exit code other than 0 (not the end of a stop), ran out of memory, or turned unhealthy.</summary>
    public bool IsProblem => Action == "oom"
        || (Action == "die" && ExitCode is { } code && code != "0" && !Attributes.ContainsKey(StoppedAttribute))
        || (Action == "health_status" && Attributes.TryGetValue("health_status", out var h) && h == "unhealthy");

    /// <summary>A command run inside a container (<c>exec_create</c>, <c>exec_start</c>, <c>exec_die</c>): mostly the probes of health checks.</summary>
    public bool IsProbe => Action.StartsWith("exec_", StringComparison.Ordinal);

    /// <summary>Action words for the eye: <c>health_status: unhealthy</c> keeps its value, <c>exec_start: sh</c> its command.</summary>
    public string Label => Attributes.TryGetValue("health_status", out var h) && Action == "health_status" ? "health: " + h : Action;
}

/// <summary>The events of a window of time, with the filters <c>docker events</c> knows (type, action, the object), newest first.</summary>
public static partial class DockerEvents
{
    /// <summary>The object types the page filters by.</summary>
    public static readonly IReadOnlyList<string> Types = ["container", "image", "volume", "network", "daemon", "plugin"];

    /// <summary>The windows the page offers, in hours.</summary>
    public static readonly IReadOnlyList<int> Windows = [1, 6, 24, 72, 168];

    /// <summary>More are not read (a guard; Docker itself keeps fewer, <see cref="DaemonKeeps"/>).</summary>
    public const int MaxEvents = 1000;

    /// <summary>How many past events the Docker daemon keeps in memory: an answer of this size has lost the older ones of its window.</summary>
    public const int DaemonKeeps = 256;

    [GeneratedRegex("^[a-z_]{2,30}$")]
    private static partial Regex ActionPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex ObjectPattern();

    /// <summary>
    /// <c>docker events</c> for the last <paramref name="hours"/> (it exits at the end of the window because of <c>--until</c>). A filter that is not one
    /// of the known types, not a plain action word or not a plain name is dropped, so nothing of it reaches the shell.
    /// </summary>
    public static string Command(string dockerHead, int hours, string? type = null, string? action = null, string? container = null)
    {
        var filters = string.Empty;
        if (type is not null && Types.Contains(type))
        {
            filters += " --filter type=" + type;
        }

        if (action is not null && ActionPattern().IsMatch(action))
        {
            filters += " --filter event=" + action;
        }

        if (container is not null && ObjectPattern().IsMatch(container))
        {
            filters += " --filter container=" + VolumeFileCommands.ShellQuote(container);
        }

        return VolumeCommands.PathPrefix + dockerHead + " events --since " + Math.Clamp(hours, 1, 168).ToString(CultureInfo.InvariantCulture)
               + "h --until 0s" + filters + " --format '{{json .}}' | tail -n " + MaxEvents.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The JSON lines of <c>docker events --format '{{json .}}'</c>, newest first; a line that is no event is skipped.</summary>
    public static IReadOnlyList<DockerEvent> Parse(string output)
    {
        var list = new List<DockerEvent>();
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] != '{')
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                var type = (Str(root, "Type") ?? "container").ToLowerInvariant();
                var action = Str(root, "Action") ?? Str(root, "status") ?? string.Empty;
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                var colon = action.IndexOf(':');
                if (colon > 0)
                {
                    // "health_status: healthy", "exec_start: sh -c ..."
                    attributes[action[..colon].Trim()] = action[(colon + 1)..].Trim();
                    action = action[..colon];
                }

                action = action.Trim().ToLowerInvariant();
                if (action.Length == 0)
                {
                    continue;
                }

                var id = Str(root, "id") ?? string.Empty;
                if (root.TryGetProperty("Actor", out var actor) && actor.ValueKind == JsonValueKind.Object)
                {
                    id = Str(actor, "ID") ?? id;
                    if (actor.TryGetProperty("Attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var a in attrs.EnumerateObject())
                        {
                            if (a.Value.ValueKind == JsonValueKind.String)
                            {
                                attributes.TryAdd(a.Name, a.Value.GetString()!);
                            }
                        }
                    }
                }

                var time = root.TryGetProperty("timeNano", out var nano) && nano.TryGetInt64(out var ns)
                    ? DateTime.UnixEpoch.AddTicks(ns / 100)
                    : root.TryGetProperty("time", out var sec) && sec.TryGetInt64(out var s) ? DateTime.UnixEpoch.AddSeconds(s) : DateTime.MinValue;

                // The name: a container's and a network's attribute "name"; an image and a volume are named by their id.
                var name = attributes.TryGetValue("name", out var n) ? n : type is "image" or "volume" ? id : null;
                list.Add(new DockerEvent(DateTime.SpecifyKind(time, DateTimeKind.Utc), type, action, id, name, attributes));
            }
            catch (JsonException)
            {
            }
        }

        // A die within a minute after a kill of the same container is the end of a stop (docker stop, compose down), as the alerts see it.
        var ordered = list.OrderBy(e => e.TimeUtc).ToList();
        var lastKill = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            if (e.Type == "container" && e.Action == "kill")
            {
                lastKill[e.Id] = e.TimeUtc;
            }
            else if (e.Type == "container" && e.Action == "die" && lastKill.Remove(e.Id, out var killed) && e.TimeUtc - killed <= TimeSpan.FromMinutes(1))
            {
                ordered[i] = e with { Attributes = new Dictionary<string, string>(e.Attributes, StringComparer.Ordinal) { [DockerEvent.StoppedAttribute] = "true" } };
            }
        }

        ordered.Reverse();
        return ordered;
    }

    /// <summary>The events that contain <paramref name="text"/> in their action, name, image or id (empty = all).</summary>
    public static IReadOnlyList<DockerEvent> Matching(IReadOnlyList<DockerEvent> events, string? text)
        => string.IsNullOrWhiteSpace(text)
            ? events
            : events.Where(e => new[] { e.Action, e.Label, e.Name, e.Image, e.Id, e.Container }
                .Any(v => v?.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) == true)).ToList();
}
