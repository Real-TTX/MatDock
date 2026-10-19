using System.Globalization;
using System.Text.Json;
using MatDock.Core.Configuration;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Volumes;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Images;

/// <summary>One layer of an image (<c>docker image history</c>): the instruction that made it and its size.</summary>
public sealed record ImageLayer(string CreatedBy, string CreatedSince, string Size, string? Comment);

/// <summary>What <c>docker image inspect</c> tells about an image, without its environment variables (they may hold secrets).</summary>
public sealed record ImageDetail(
    string Id,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Digests,
    DateTime? CreatedUtc,
    long SizeBytes,
    string Platform,
    string? Entrypoint,
    string? Command,
    string? WorkingDir,
    string? User,
    IReadOnlyList<string> ExposedPorts,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<ImageLayer> Layers);

/// <summary>Reads the details and the layers of one image on an environment.</summary>
public sealed class ImageService
{
    private readonly IHostSessionFactory _sessions;
    private readonly EnvironmentService _environments;
    private readonly MatDockOptions _options;

    public ImageService(IHostSessionFactory sessions, EnvironmentService environments, IOptions<MatDockOptions> options)
    {
        _sessions = sessions;
        _environments = environments;
        _options = options.Value;
    }

    /// <summary>The image, or an error text (invalid reference, not found, host unreachable).</summary>
    public async Task<(ImageDetail? Detail, string? Error)> GetDetailAsync(DockerEnvironment env, string reference, CancellationToken ct = default)
    {
        if (!ImageCommands.IsValidImageRef(reference))
        {
            return (null, "Invalid image reference.");
        }

        var settings = _environments.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        try
        {
            using var session = _sessions.Create(settings);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)));
                await session.ConnectAsync(connect.Token);
            }

            var inspect = await RunAsync(session, ImageCommands.Inspect(reference, head), ct);
            if (inspect.Exit != 0)
            {
                return (null, DockerErrorMessages.InterpretDockerError(inspect.Err, inspect.Out));
            }

            var history = await RunAsync(session, ImageCommands.History(reference, head), ct);
            var layers = history.Exit == 0 ? ParseHistory(history.Out) : Array.Empty<ImageLayer>();
            var detail = ParseInspect(inspect.Out, layers);
            return detail is null ? (null, "The image could not be read.") : (detail, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, ex.Message);
        }
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(IHostSession session, string command, CancellationToken ct)
    {
        using var cmd = session.CreateCommand(command);
        var timeout = TimeSpan.FromSeconds(Math.Max(10, _options.SshTimeoutSeconds));
        cmd.CommandTimeout = timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await cmd.ExecuteAsync(cts.Token);
        return (cmd.ExitStatus ?? -1, cmd.Result ?? string.Empty, cmd.Error ?? string.Empty);
    }

    public static IReadOnlyList<ImageLayer> ParseHistory(string output)
    {
        var layers = new List<ImageLayer>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var createdBy = Str(root, "CreatedBy") ?? string.Empty;
                // The legacy builder writes "/bin/sh -c #(nop) " before metadata instructions.
                createdBy = createdBy.Replace("/bin/sh -c #(nop) ", string.Empty, StringComparison.Ordinal).Trim();
                layers.Add(new ImageLayer(createdBy, Str(root, "CreatedSince") ?? string.Empty, Str(root, "Size") ?? string.Empty,
                    string.IsNullOrWhiteSpace(Str(root, "Comment")) ? null : Str(root, "Comment")));
            }
            catch (JsonException)
            {
                // not a history line
            }
        }

        return layers;
    }

    public static ImageDetail? ParseInspect(string output, IReadOnlyList<ImageLayer> layers)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var config = root.TryGetProperty("Config", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            DateTime? created = DateTime.TryParse(Str(root, "Created"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : null;
            var labels = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in l.EnumerateObject())
                {
                    labels[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.ToString();
                }
            }

            var ports = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("ExposedPorts", out var ep) && ep.ValueKind == JsonValueKind.Object
                ? ep.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList()
                : new List<string>();
            var os = Str(root, "Os") ?? "?";
            var arch = Str(root, "Architecture") ?? "?";
            var variant = Str(root, "Variant");

            return new ImageDetail(
                Str(root, "Id") ?? string.Empty,
                Strings(root, "RepoTags"),
                Strings(root, "RepoDigests"),
                created,
                root.TryGetProperty("Size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                variant is null ? $"{os}/{arch}" : $"{os}/{arch}/{variant}",
                Joined(config, "Entrypoint"),
                Joined(config, "Cmd"),
                config.ValueKind == JsonValueKind.Object ? NullIfEmpty(Str(config, "WorkingDir")) : null,
                config.ValueKind == JsonValueKind.Object ? NullIfEmpty(Str(config, "User")) : null,
                ports,
                labels,
                layers);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : Array.Empty<string>();

    private static string? Joined(JsonElement config, string name)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parts = Strings(config, name);
        return parts.Count == 0 ? null : string.Join(' ', parts.Select(p => p.Contains(' ') ? $"\"{p}\"" : p));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
