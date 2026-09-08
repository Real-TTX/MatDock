using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Execution;
using MatDock.Core.Images;
using MatDock.Core.Networks;
using MatDock.Core.Ssh;
using MatDock.Core.Volumes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Environments;

/// <summary>
/// Connectivity operations against a Docker host — remote over SSH or the local socket. It runs the
/// Docker CLI (structured <c>--format '{{json .}}'</c> output) so failures produce clear, actionable
/// messages (Docker missing, no daemon, missing socket permission) instead of an opaque tunnel error.
/// </summary>
public sealed class EnvironmentConnectionService : IEnvironmentConnectionService
{
    private readonly IHostSessionFactory _hostSessionFactory;
    private readonly MatDockOptions _options;
    private readonly ILogger<EnvironmentConnectionService> _logger;

    public EnvironmentConnectionService(
        IHostSessionFactory hostSessionFactory,
        IOptions<MatDockOptions> options,
        ILogger<EnvironmentConnectionService> logger)
    {
        _hostSessionFactory = hostSessionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DockerConnectionResult> TestConnectionAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _hostSessionFactory.Create(settings);
            await ConnectAsync(client, settings, cancellationToken);

            // Auto-detect how Docker is reachable on this host and return the working access.
            // Keep the FIRST (default) probe's error for the failure message — it is the most
            // representative cause; the later sudo/rootless probes tend to mask it.
            (int ExitStatus, string StdOut, string StdErr) primaryProbe = (-1, string.Empty, string.Empty);
            var isFirstProbe = true;
            foreach (var (useSudo, dockerHost) in BuildCandidates(client, settings))
            {
                var head = VolumeCommands.DockerHead(useSudo, dockerHost);
                var probe = RunCommand(client, VolumeCommands.ServerVersion(head), settings);
                if (probe.ExitStatus == 0 && !string.IsNullOrWhiteSpace(probe.StdOut))
                {
                    var (version, apiVersion, osArch) = ParseServerVersion(probe.StdOut);
                    var label = AccessLabel(useSudo, dockerHost);
                    return DockerConnectionResult.Ok(
                        $"Connected to Docker {version} (API {apiVersion}) · Access: {label}.",
                        version, apiVersion, osArch, useSudo, dockerHost, label);
                }

                if (isFirstProbe)
                {
                    primaryProbe = probe;
                    isFirstProbe = false;
                }
            }

            return DockerConnectionResult.Fail(DockerErrorMessages.InterpretDockerError(primaryProbe.StdErr, primaryProbe.StdOut));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DockerConnectionResult.Fail("Connection timed out.", EnvironmentStatus.Offline);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Connection test to {Host}:{Port} failed.", settings.Host, settings.Port);
            return DockerConnectionResult.Fail(DockerErrorMessages.DescribeSshError(ex), EnvironmentStatus.Error);
        }
    }

    public async Task<IReadOnlyList<DockerVolume>> ListVolumesAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            // Turn raw SSH errors (e.g. "Permission denied (password)") into an actionable message.
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, VolumeCommands.VolumeList(head), settings);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
        }

        return ParseVolumes(result.StdOut);
    }

    public async Task<(IReadOnlyList<DockerVolume> Volumes, IReadOnlyCollection<string> UnusedNames)> ListVolumesWithUsageAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, VolumeCommands.VolumeList(head), settings);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
        }

        var volumes = ParseVolumes(result.StdOut);

        // Best-effort on the same connection: which volumes are unused (dangling). A probe failure must
        // not fail the listing — the page still shows the volumes, just without the "unused" marker.
        var unused = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var dangling = RunCommand(client, VolumeCommands.VolumeListDangling(head), settings);
            if (dangling.ExitStatus == 0)
            {
                foreach (var line in dangling.StdOut.Split('\n'))
                {
                    var name = line.Trim();
                    if (name.Length > 0)
                    {
                        unused.Add(name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dangling-volume probe failed for host {Host}.", settings.Host);
        }

        return (volumes, unused);
    }

    public async Task<DockerVolumeDetail?> InspectVolumeAsync(SshConnectionSettings settings, string volume, CancellationToken cancellationToken = default)
    {
        if (!VolumeCommands.IsValidVolumeName(volume))
        {
            return null;
        }

        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, VolumeCommands.InspectJson(volume, head), settings);
        return result.ExitStatus != 0 ? null : ParseVolumeDetail(result.StdOut);
    }

    private static DockerVolumeDetail? ParseVolumeDetail(string json)
    {
        var line = DockerErrorMessages.FirstLine(json);
        if (line is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

            IReadOnlyDictionary<string, string> Obj(string name)
            {
                var map = new Dictionary<string, string>();
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in v.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            map[p.Name] = p.Value.GetString()!;
                        }
                    }
                }

                return map;
            }

            return new DockerVolumeDetail
            {
                Name = Get("Name") ?? string.Empty,
                Driver = Get("Driver") ?? string.Empty,
                Mountpoint = Get("Mountpoint"),
                Scope = Get("Scope"),
                CreatedAt = Get("CreatedAt"),
                Options = Obj("Options"),
                Labels = Obj("Labels"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<(bool Ok, string Message)> CreateVolumeAsync(SshConnectionSettings settings, string name, CancellationToken cancellationToken = default)
    {
        if (!VolumeCommands.IsValidVolumeName(name))
        {
            return (false, "Invalid volume name (letters, digits and . _ - allowed, must start alphanumeric).");
        }

        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            return (false, DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, VolumeCommands.Create(name, head), settings);
        return result.ExitStatus == 0
            ? (true, $"Volume \"{name}\" created.")
            : (false, DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
    }

    public async Task<(bool Ok, string Message)> CreateVolumeAsync(SshConnectionSettings settings, string name, string? driver, IReadOnlyList<(string Key, string Value)> options, CancellationToken cancellationToken = default)
    {
        if (!VolumeCommands.IsValidVolumeName(name))
        {
            return (false, "Invalid volume name (letters, digits and . _ - allowed, must start alphanumeric).");
        }

        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            return (false, DockerErrorMessages.DescribeSshError(ex));
        }

        string command;
        try
        {
            var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
            command = VolumeCommands.CreateWithOptions(name, driver, options, head);
        }
        catch (ArgumentException ex)
        {
            return (false, ex.Message);
        }

        var result = RunCommand(client, command, settings);
        return result.ExitStatus == 0
            ? (true, $"Volume \"{name}\" created.")
            : (false, DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
    }

    public async Task<HostStats> GetHostStatsAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, HostStats.BuildCommand(head), settings);
        return HostStats.Parse(result.StdOut);
    }

    public async Task<IReadOnlyList<DockerUnusedVolume>> ListUnusedVolumesDetailedAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, VolumeCommands.InspectDangling(head), settings);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
        }

        var list = new List<DockerUnusedVolume>();
        foreach (var line in result.StdOut.Split('\n'))
        {
            var detail = ParseVolumeDetail(line);
            if (detail is not null && !string.IsNullOrEmpty(detail.Name))
            {
                list.Add(new DockerUnusedVolume(detail.Name, IsNetworkShare(detail.Options)));
            }
        }

        return list;
    }

    public async Task<PruneResult> RemoveVolumesAsync(SshConnectionSettings settings, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        if (names.Count == 0)
        {
            return new PruneResult(true, 0, "Nothing selected.");
        }

        string command;
        try
        {
            var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
            command = VolumeCommands.RemoveVolumes(names, head);
        }
        catch (ArgumentException ex)
        {
            return PruneResult.Fail(ex.Message);
        }

        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            return PruneResult.Fail(DockerErrorMessages.DescribeSshError(ex));
        }

        var result = RunCommand(client, command, settings);
        // `docker volume rm` prints each removed name to stdout; a non-zero exit means at least one failed.
        var removed = result.StdOut.Split('\n').Count(l => l.Trim().Length > 0);
        return result.ExitStatus == 0
            ? new PruneResult(true, removed, result.StdOut.Trim())
            : new PruneResult(false, removed, DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
    }

    /// <summary>A volume is a remote network share when its driver "type" option is a network filesystem.</summary>
    private static bool IsNetworkShare(IReadOnlyDictionary<string, string> options)
        => options.TryGetValue("type", out var t)
           && t.ToLowerInvariant() is "nfs" or "nfs4" or "cifs" or "smb" or "smbfs";

    public async Task<(IReadOnlyList<DockerNetwork> Networks, IReadOnlyCollection<string> UnusedNames)> ListNetworksWithUsageAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, NetworkCommands.List(head), settings);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
        }

        var networks = ParseNetworks(result.StdOut);

        // Best-effort on the same connection: which networks are unused (dangling / removable by prune).
        var unused = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var dangling = RunCommand(client, NetworkCommands.ListDangling(head), settings);
            if (dangling.ExitStatus == 0)
            {
                foreach (var line in dangling.StdOut.Split('\n'))
                {
                    var name = line.Trim();
                    if (name.Length > 0)
                    {
                        unused.Add(name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dangling-network probe failed for host {Host}.", settings.Host);
        }

        return (networks, unused);
    }

    public async Task<DockerNetworkDetail?> InspectNetworkAsync(SshConnectionSettings settings, string nameOrId, CancellationToken cancellationToken = default)
    {
        if (!NetworkCommands.IsValidNetworkRef(nameOrId))
        {
            return null;
        }

        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, NetworkCommands.InspectJson(nameOrId, head), settings);
        return result.ExitStatus != 0 ? null : ParseNetworkDetail(result.StdOut);
    }

    public async Task<(bool Ok, string Message)> RemoveNetworkAsync(SshConnectionSettings settings, string nameOrId, CancellationToken cancellationToken = default)
    {
        if (!NetworkCommands.IsValidNetworkRef(nameOrId))
        {
            return (false, "Invalid network name/id.");
        }

        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            return (false, DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, NetworkCommands.Remove(nameOrId, head), settings);
        return result.ExitStatus == 0
            ? (true, $"Network \"{nameOrId}\" removed.")
            : (false, DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
    }

    public async Task<PruneResult> PruneNetworksAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            return PruneResult.Fail(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, NetworkCommands.Prune(head), settings);
        return result.ExitStatus == 0
            ? new PruneResult(true, CountPruneItems(result.StdOut), result.StdOut.Trim())
            : PruneResult.Fail(DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
    }

    public async Task<IReadOnlyList<DockerImage>> ListImagesAsync(SshConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            throw new InvalidOperationException(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, ImageCommands.List(head), settings);
        if (result.ExitStatus != 0)
        {
            throw new InvalidOperationException(DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
        }

        return ParseImages(result.StdOut);
    }

    public async Task<(bool Ok, string Message)> RemoveImageAsync(SshConnectionSettings settings, string reference, bool force, CancellationToken cancellationToken = default)
    {
        if (!ImageCommands.IsValidImageRef(reference))
        {
            return (false, "Invalid image reference.");
        }

        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            return (false, DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, ImageCommands.Remove(reference, force, head), settings);
        return result.ExitStatus == 0
            ? (true, $"Image \"{reference}\" removed.")
            : (false, DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
    }

    public async Task<PruneResult> PruneImagesAsync(SshConnectionSettings settings, bool all, CancellationToken cancellationToken = default)
    {
        using var client = _hostSessionFactory.Create(settings);
        try
        {
            await ConnectAsync(client, settings, cancellationToken);
        }
        catch (Exception ex) when (DockerErrorMessages.IsSshError(ex))
        {
            return PruneResult.Fail(DockerErrorMessages.DescribeSshError(ex));
        }

        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var result = RunCommand(client, ImageCommands.Prune(all, head), settings);
        return result.ExitStatus == 0
            ? new PruneResult(true, CountPruneItems(result.StdOut), result.StdOut.Trim())
            : PruneResult.Fail(DockerErrorMessages.InterpretDockerError(result.StdErr, result.StdOut));
    }

    /// <summary>Counts the deleted items in a <c>docker … prune</c> output (item lines carry no colon).</summary>
    private static int CountPruneItems(string stdout)
        => stdout.Split('\n')
            .Select(l => l.Trim())
            .Count(l => l.Length > 0 && !l.Contains(':'));

    /// <summary>Ordered access strategies to probe: default, sudo, then any discovered rootless sockets.</summary>
    private IReadOnlyList<(bool UseSudo, string? DockerHost)> BuildCandidates(IHostSession client, SshConnectionSettings settings)
    {
        var candidates = new List<(bool, string?)>
        {
            (false, null),
            (true, null),
        };

        var listed = RunCommand(client, "ls -d /run/user/*/docker.sock 2>/dev/null", settings);
        if (listed.ExitStatus == 0)
        {
            foreach (var line in listed.StdOut.Split('\n'))
            {
                var path = line.Trim();
                if (path.Length == 0)
                {
                    continue;
                }

                var host = $"unix://{path}";
                if (VolumeCommands.IsValidDockerHost(host))
                {
                    candidates.Add((false, host));
                    candidates.Add((true, host));
                }
            }
        }

        return candidates;
    }

    private static string AccessLabel(bool useSudo, string? dockerHost)
    {
        if (dockerHost is not null)
        {
            var path = dockerHost.StartsWith("unix://", StringComparison.Ordinal) ? dockerHost[7..] : dockerHost;
            return useSudo ? $"rootless+sudo ({path})" : $"rootless ({path})";
        }

        return useSudo ? "sudo" : "Default";
    }

    private async Task ConnectAsync(IHostSession client, SshConnectionSettings settings, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)));
        await client.ConnectAsync(cts.Token);
    }

    private (int ExitStatus, string StdOut, string StdErr) RunCommand(IHostSession client, string command, SshConnectionSettings settings)
    {
        using var sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = TimeSpan.FromSeconds(Math.Max(5, _options.SshTimeoutSeconds));
        var stdout = sshCommand.Execute();
        return (sshCommand.ExitStatus ?? -1, stdout ?? string.Empty, sshCommand.Error ?? string.Empty);
    }

    private static (string Version, string ApiVersion, string OsArch) ParseServerVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "?";
            var os = Get("Os");
            var arch = Get("Arch");
            return (Get("Version"), Get("ApiVersion"), $"{os}/{arch}");
        }
        catch (JsonException)
        {
            return ("?", "?", "?");
        }
    }

    private static IReadOnlyList<DockerVolume> ParseVolumes(string output)
    {
        var volumes = new List<DockerVolume>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

                volumes.Add(new DockerVolume
                {
                    Name = Get("Name") ?? string.Empty,
                    Driver = Get("Driver") ?? string.Empty,
                    Mountpoint = Get("Mountpoint"),
                    Scope = Get("Scope"),
                    Labels = ParseLabels(Get("Labels")),
                });
            }
            catch (JsonException)
            {
                // Skip malformed lines rather than failing the whole listing.
            }
        }

        return volumes
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> ParseLabels(string? labels)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(labels))
        {
            return result;
        }

        foreach (var pair in labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0)
            {
                result[pair[..eq]] = pair[(eq + 1)..];
            }
        }

        return result;
    }

    private static IReadOnlyList<DockerImage> ParseImages(string output)
    {
        var images = new List<DockerImage>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

                images.Add(new DockerImage
                {
                    Id = Get("ID") ?? string.Empty,
                    Repository = Get("Repository") ?? string.Empty,
                    Tag = Get("Tag") ?? string.Empty,
                    Digest = Get("Digest") is { Length: > 0 } d && d != "<none>" ? d : null,
                    CreatedAt = Get("CreatedAt"),
                    CreatedSince = Get("CreatedSince"),
                    Size = Get("Size"),
                });
            }
            catch (JsonException)
            {
                // Skip malformed lines rather than failing the whole listing.
            }
        }

        // Tagged images first (alphabetical), dangling ones last.
        return images
            .OrderBy(i => i.Dangling)
            .ThenBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<DockerNetwork> ParseNetworks(string output)
    {
        var networks = new List<DockerNetwork>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

                // `docker network ls --format json` reports Internal as the string "true"/"false".
                var isInternal = string.Equals(Get("Internal"), "true", StringComparison.OrdinalIgnoreCase);

                networks.Add(new DockerNetwork
                {
                    Id = Get("ID") ?? string.Empty,
                    Name = Get("Name") ?? string.Empty,
                    Driver = Get("Driver") ?? string.Empty,
                    Scope = Get("Scope"),
                    Internal = isInternal,
                    CreatedAt = Get("CreatedAt"),
                    Labels = ParseLabels(Get("Labels")),
                });
            }
            catch (JsonException)
            {
                // Skip malformed lines rather than failing the whole listing.
            }
        }

        return networks
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DockerNetworkDetail? ParseNetworkDetail(string json)
    {
        var line = DockerErrorMessages.FirstLine(json);
        if (line is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            IReadOnlyDictionary<string, string> Obj(string name)
            {
                var map = new Dictionary<string, string>();
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in v.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            map[p.Name] = p.Value.GetString()!;
                        }
                    }
                }

                return map;
            }

            // IPAM.Config → "subnet → gateway" strings.
            var subnets = new List<string>();
            if (root.TryGetProperty("IPAM", out var ipam) && ipam.ValueKind == JsonValueKind.Object
                && ipam.TryGetProperty("Config", out var cfg) && cfg.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cfg.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var subnet = c.TryGetProperty("Subnet", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    var gateway = c.TryGetProperty("Gateway", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                    if (!string.IsNullOrEmpty(subnet))
                    {
                        subnets.Add(string.IsNullOrEmpty(gateway) ? subnet! : $"{subnet} → {gateway}");
                    }
                }
            }

            // Containers is an object map (id → { Name, … }); collect the container names.
            var containers = new List<string>();
            if (root.TryGetProperty("Containers", out var cs) && cs.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in cs.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Object
                        && p.Value.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        containers.Add(n.GetString()!);
                    }
                }
            }

            return new DockerNetworkDetail
            {
                Id = Str("Id") ?? string.Empty,
                Name = Str("Name") ?? string.Empty,
                Driver = Str("Driver") ?? string.Empty,
                Scope = Str("Scope"),
                Internal = Flag("Internal"),
                Attachable = Flag("Attachable"),
                EnableIPv6 = Flag("EnableIPv6"),
                CreatedAt = Str("Created"),
                Subnets = subnets,
                Containers = containers.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(),
                Options = Obj("Options"),
                Labels = Obj("Labels"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
