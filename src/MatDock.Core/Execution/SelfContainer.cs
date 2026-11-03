using System.Text.RegularExpressions;

namespace MatDock.Core.Execution;

/// <summary>
/// The container MatDock itself runs in, when it runs in Docker: its id (from <c>/proc/self/mountinfo</c>, where Docker mounts the container's
/// <c>hostname</c> and <c>hosts</c> files from <c>…/containers/&lt;id&gt;/</c>). A stack or container with this id is MatDock: stopping it from MatDock
/// would leave nobody to start it again, and updating it the ordinary way would stop the process that runs the update (<see cref="Stacks.SelfHandover"/>).
/// </summary>
public sealed partial class SelfContainer
{
    public SelfContainer()
        : this(Detect(TryRead("/proc/self/mountinfo")))
    {
    }

    public SelfContainer(string? id) => Id = id;

    /// <summary>The full id (64 hex characters), or null when MatDock does not run in a container (or it cannot be told).</summary>
    public string? Id { get; set; }

    // Per environment (and its connection): where MatDock's container is there, or null when it is not on it. Only certain answers are kept.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SelfPlace?> _places = new(StringComparer.Ordinal);

    /// <summary>What is known about an environment, keyed by <paramref name="key"/> (its id and connection); false when nothing is known yet.</summary>
    public bool TryGetPlace(string key, out SelfPlace? place) => _places.TryGetValue(key, out place);

    public void SetPlace(string key, SelfPlace? place) => _places[key] = place;

    /// <summary>
    /// Reads the answer of <see cref="Stacks.SelfHandover.Inspect"/>: the place (<c>project|service</c>) on success, null when Docker says the container
    /// is not there; <paramref name="certain"/> is false for any other failure (the daemon unreachable), which must not be remembered.
    /// </summary>
    public static SelfPlace? ParsePlace(bool ok, string? output, out bool certain)
    {
        var text = (output ?? string.Empty).Trim();
        if (!ok)
        {
            certain = text.Contains("No such object", StringComparison.OrdinalIgnoreCase) || text.Contains("No such container", StringComparison.OrdinalIgnoreCase);
            return null;
        }

        certain = true;
        var line = text.Split('\n').LastOrDefault()?.Trim() ?? string.Empty;
        var bar = line.IndexOf('|');
        var project = bar < 0 ? line : line[..bar];
        var service = bar < 0 ? string.Empty : line[(bar + 1)..];
        return new SelfPlace(project is "<no value>" ? string.Empty : project, service is "<no value>" ? string.Empty : service);
    }

    // "… /var/lib/docker/containers/<id>/hostname /etc/hostname rw,…": the files Docker gives every container, at their place in /etc.
    [GeneratedRegex(@"/containers/([0-9a-f]{64})/(?:hostname|hosts|resolv\.conf) /etc/")]
    private static partial Regex ContainerPath();

    public static string? Detect(string? mountinfo)
        => mountinfo is null ? null : ContainerPath().Match(mountinfo) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Whether a container id (full or the short 12 characters of <c>docker ps</c>) is MatDock's own.</summary>
    public bool Is(string? containerId)
        => Id is not null && containerId is { Length: >= 12 } && Id.StartsWith(containerId.Trim().ToLowerInvariant(), StringComparison.Ordinal);

    // Released when a step was handed to the helper container: when compose changes nothing, MatDock keeps running and reads the result itself.
    private readonly SemaphoreSlim _handedOver = new(0);

    /// <summary>Says that a step was handed to the helper container (<see cref="Stacks.SelfHandover"/>).</summary>
    public void NoteHandover() => _handedOver.Release();

    /// <summary>Waits until a step is handed over (<see cref="NoteHandover"/>).</summary>
    public Task WaitForHandoverAsync(CancellationToken ct) => _handedOver.WaitAsync(ct);

    // Through a bridge: the MatDock at the end of the chain, per environment, for a while.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (SelfTarget? Target, DateTime At)> _bridged = new(StringComparer.Ordinal);

    /// <summary>How long the answer of a bridge about its MatDock is kept.</summary>
    public static readonly TimeSpan BridgeAnswerFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The MatDock container to protect on an environment, and how a helper container reaches Docker there: for a direct environment this MatDock's own
    /// container; through a bridge the MatDock at the end of the chain (the commands run through it: stopped, it would cut its own bridge mid-command, and
    /// steps of its stack go to a helper container there). Null when there is none, or the bridge does not answer now.
    /// </summary>
    public async Task<SelfTarget?> TargetAsync(long environmentId, Ssh.SshConnectionSettings settings, CancellationToken ct)
    {
        if (!settings.IsBridge)
        {
            return Id is null ? null : new SelfTarget(Id, settings);
        }

        var key = PlaceKey(environmentId, settings);
        if (_bridged.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < BridgeAnswerFor)
        {
            return known.Target;
        }

        try
        {
            var info = await BridgeHttp.InfoAsync(settings, ct);
            var target = info.MatDockContainer is { Length: 64 } id && id.All(Uri.IsHexDigit)
                ? new SelfTarget(id.ToLowerInvariant(), new Ssh.SshConnectionSettings
                {
                    ConnectionType = info.Connection switch { "local" => Entities.ConnectionType.Local, "tcp" => Entities.ConnectionType.Tcp, _ => Entities.ConnectionType.Ssh },
                    Host = "bridge",
                    Username = string.Empty,
                    UseSudo = info.UseSudo,
                    DockerHost = info.DockerHost,
                })
                : null;
            _bridged[key] = (target, DateTime.UtcNow);
            return target;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>For the pages: whether a container of an environment is the MatDock to protect there (this one, or the one behind its bridge, when known).</summary>
    public bool IsOn(long environmentId, string? containerId)
        => Is(containerId) || (containerId is { Length: >= 12 } c && _bridged.Any(kv => kv.Key.StartsWith(environmentId + "|", StringComparison.Ordinal)
                                                                                       && kv.Value.Target?.Id.StartsWith(c.Trim().ToLowerInvariant(), StringComparison.Ordinal) == true));

    /// <summary>The key of an environment's answer: its id and what it connects to (an edited connection asks again).</summary>
    public static string PlaceKey(long environmentId, Ssh.SshConnectionSettings settings)
        => $"{environmentId}|{settings.ConnectionType}|{settings.Host}|{settings.Port}|{settings.DockerHost}";

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Where MatDock's container is on an environment: its compose project and service (empty when it was not made by compose).</summary>
public sealed record SelfPlace(string Project, string Service);

/// <summary>
/// The MatDock container to protect on an environment (<see cref="SelfContainer.TargetAsync"/>) and the connection a helper container works with there
/// (<see cref="Stacks.SelfHandover.For"/>): through a bridge the kind of the environment at its end.
/// </summary>
public sealed record SelfTarget(string Id, Ssh.SshConnectionSettings Handover)
{
    public Stacks.SelfHandover? HandOver() => Stacks.SelfHandover.For(Handover, Id);

    public bool Is(string? containerId) => new SelfContainer(Id).Is(containerId);
}
