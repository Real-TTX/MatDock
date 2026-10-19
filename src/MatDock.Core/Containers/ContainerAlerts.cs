using System.Globalization;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Volumes;

namespace MatDock.Core.Containers;

/// <summary>Something about a container worth telling someone: it died with an error, or its health check failed.</summary>
public sealed record ContainerAlert(ScheduleEvent Event, string ContainerName, string? Project, string Detail, DateTime TimeUtc);

/// <summary>
/// Finds alerts in a window of <c>docker events</c>: a <c>die</c> with an exit code other than 0 (or after an <c>oom</c>)
/// that no <c>kill</c> shortly before explains (docker stop, compose down and restart send one), and a health check that
/// turned <c>unhealthy</c>. One alert per container and kind per window, so a crash loop is one message per check.
/// </summary>
public static class ContainerAlerts
{
    /// <summary>A die this long after a kill of the same container is the end of a stop, not a crash.</summary>
    private const long KillGraceSeconds = 60;

    /// <summary>The container events between two Unix times; returns when the window is read (both bounds set).</summary>
    public static string EventsCommand(string dockerHead, long sinceUnix, long untilUnix)
        => VolumeCommands.PathPrefix + dockerHead
           + " events --since " + sinceUnix.ToString(CultureInfo.InvariantCulture)
           + " --until " + untilUnix.ToString(CultureInfo.InvariantCulture)
           + " --filter type=container --filter event=start --filter event=kill --filter event=oom --filter event=die"
           + " --filter event=health_status --format '{{json .}}'";

    public static IReadOnlyList<ContainerAlert> Detect(string eventsOutput, string environmentName)
    {
        var events = Parse(eventsOutput).OrderBy(e => e.TimeNano).ToList();
        var lastKill = new Dictionary<string, long>(StringComparer.Ordinal);
        var oom = new HashSet<string>(StringComparer.Ordinal);
        var alerts = new List<ContainerAlert>();
        var seen = new HashSet<(ScheduleEvent, string)>();

        foreach (var e in events)
        {
            var seconds = e.TimeNano / 1_000_000_000;
            switch (e.Action)
            {
                case "kill":
                    lastKill[e.Id] = seconds;
                    break;
                case "oom":
                    oom.Add(e.Id);
                    break;
                case "start":
                    oom.Remove(e.Id);
                    break;
                case "die":
                {
                    var outOfMemory = oom.Remove(e.Id);
                    var stopped = lastKill.TryGetValue(e.Id, out var killed) && seconds - killed <= KillGraceSeconds;
                    lastKill.Remove(e.Id);
                    var code = e.ExitCode ?? 0;
                    if ((stopped && !outOfMemory) || (code == 0 && !outOfMemory) || !seen.Add((ScheduleEvent.ContainerDied, e.Id)))
                    {
                        break;
                    }

                    var why = outOfMemory ? $"was killed for lack of memory (exit code {code})" : $"exited with code {code}";
                    alerts.Add(new ContainerAlert(ScheduleEvent.ContainerDied, e.Name, e.Project,
                        $"Container {e.Name} {why} on {environmentName}.", e.TimeUtc));
                    break;
                }
                case "health_status: unhealthy":
                    if (seen.Add((ScheduleEvent.ContainerUnhealthy, e.Id)))
                    {
                        alerts.Add(new ContainerAlert(ScheduleEvent.ContainerUnhealthy, e.Name, e.Project,
                            $"Container {e.Name} is unhealthy on {environmentName}.", e.TimeUtc));
                    }
                    break;
            }
        }

        return alerts;
    }

    private sealed record RawEvent(string Id, string Action, string Name, string? Project, int? ExitCode, long TimeNano)
    {
        public DateTime TimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(TimeNano / 1_000_000).UtcDateTime;
    }

    private static IEnumerable<RawEvent> Parse(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }

            RawEvent? parsed = null;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var action = (Str(root, "Action") ?? Str(root, "status") ?? string.Empty).Trim().ToLowerInvariant();
                var id = Str(root, "id") ?? string.Empty;
                string? name = null, project = null, exitCode = null;
                if (root.TryGetProperty("Actor", out var actor) && actor.ValueKind == JsonValueKind.Object)
                {
                    id = Str(actor, "ID") ?? id;
                    if (actor.TryGetProperty("Attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                    {
                        name = Str(attrs, "name");
                        project = Str(attrs, "com.docker.compose.project");
                        exitCode = Str(attrs, "exitCode");
                    }
                }

                long nano = 0;
                if (root.TryGetProperty("timeNano", out var tn) && tn.ValueKind == JsonValueKind.Number)
                {
                    nano = tn.GetInt64();
                }
                else if (root.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
                {
                    nano = t.GetInt64() * 1_000_000_000;
                }

                if (id.Length > 0 && action.Length > 0)
                {
                    parsed = new RawEvent(id, action, name ?? id[..Math.Min(12, id.Length)], project,
                        int.TryParse(exitCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : null, nano);
                }
            }
            catch (JsonException)
            {
                // not an event line
            }

            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
