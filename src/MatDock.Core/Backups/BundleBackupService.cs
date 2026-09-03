using System.Text;
using System.Text.Json;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Stacks;
using MatDock.Core.Volumes;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Backups;

/// <summary>A full stack/app backup manifest (stored as a .mdbundle.json file next to its volume archives).</summary>
public sealed class BundleManifest
{
    public string Kind { get; set; } = "stack";
    public string Name { get; set; } = string.Empty;
    public long SourceEnvironmentId { get; set; }
    public string SourceEnvironmentName { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    /// <summary>"managed" (real stored compose) or "generated" (best-effort reconstruction from running containers).</summary>
    public string ComposeSource { get; set; } = "generated";
    public string Compose { get; set; } = string.Empty;
    public List<BundleService> Services { get; set; } = new();
    public List<BundleVolume> Volumes { get; set; } = new();
}

public sealed class BundleService
{
    public string Service { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
}

public sealed class BundleVolume
{
    public string Name { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

/// <summary>
/// Full backups of a whole stack/app: its volumes (as tar archives), the compose definition and the image
/// references (images are pulled again on restore — <c>docker compose up</c> handles that). A restore
/// re-creates the volumes, then recreates the stack from the compose and deploys it.
/// </summary>
public sealed class BundleBackupService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ContainerService _containerService;
    private readonly StackService _stackService;
    private readonly VolumeBackupService _volumeBackupService;
    private readonly IBackupStorageFactory _storageFactory;
    private readonly ILogger<BundleBackupService> _logger;

    public BundleBackupService(
        ContainerService containerService,
        StackService stackService,
        VolumeBackupService volumeBackupService,
        IBackupStorageFactory storageFactory,
        ILogger<BundleBackupService> logger)
    {
        _containerService = containerService;
        _stackService = stackService;
        _volumeBackupService = volumeBackupService;
        _storageFactory = storageFactory;
        _logger = logger;
    }

    /// <summary>Backs up a whole stack (all its containers' volumes + compose + image refs) to a target.</summary>
    public async Task<(bool Ok, string Message)> BackupStackAsync(DockerEnvironment env, string projectName, BackupTarget? target, bool stopContainers, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(projectName))
        {
            return (false, "Invalid stack/project name.");
        }

        var containers = await _containerService.ListByProjectAsync(env, projectName, ct);
        if (containers.Count == 0)
        {
            return (false, "No containers found for this stack.");
        }

        var services = new List<BundleService>();
        var serviceVolumes = new List<(string Service, string Volume, string Dest)>();
        var volumeSet = new List<string>();
        foreach (var c in containers)
        {
            var svc = string.IsNullOrEmpty(c.Service) ? c.Name : c.Service;
            services.Add(new BundleService { Service = svc, Image = c.Image, ContainerName = c.Name });

            var mounts = await _containerService.GetMountsAsync(env, c.Id, ct);
            foreach (var m in mounts.Where(m => m.IsVolume && !string.IsNullOrEmpty(m.Name)))
            {
                serviceVolumes.Add((svc, m.Name!, m.Destination));
                if (!volumeSet.Contains(m.Name!))
                {
                    volumeSet.Add(m.Name!);
                }
            }
        }

        // Prefer the real stored compose of a managed inline stack; otherwise reconstruct a best-effort one.
        var managed = (await _stackService.GetAllAsync(ct)).FirstOrDefault(s =>
            s.EnvironmentId == env.Id &&
            string.Equals(s.Name, projectName, StringComparison.OrdinalIgnoreCase) &&
            !s.IsGitBacked && !string.IsNullOrWhiteSpace(s.ComposeYaml));

        string compose;
        string composeSource;
        if (managed is not null)
        {
            compose = managed.ComposeYaml;
            composeSource = "managed";
        }
        else
        {
            compose = GenerateCompose(services, serviceVolumes, containers);
            composeSource = "generated";
        }

        var ts = DateTime.UtcNow;
        var volumes = new List<BundleVolume>();
        int ok = 0, fail = 0;
        foreach (var vol in volumeSet)
        {
            var res = await _volumeBackupService.BackupAsync(env, vol, target, scheduleId: null, ct: ct, stopContainers: stopContainers);
            if (res.Success && res.BackupId is { } bid)
            {
                var rec = await _volumeBackupService.GetAsync(bid, ct);
                volumes.Add(new BundleVolume { Name = vol, File = rec?.FileName ?? string.Empty, SizeBytes = res.BytesTransferred });
                ok++;
            }
            else
            {
                fail++;
            }
        }

        var manifest = new BundleManifest
        {
            Kind = "stack",
            Name = projectName,
            SourceEnvironmentId = env.Id,
            SourceEnvironmentName = env.Name,
            CreatedUtc = ts,
            ComposeSource = composeSource,
            Compose = compose,
            Services = services,
            Volumes = volumes
        };

        var manifestName = $"{projectName}_{ts:yyyyMMddHHmmss}{BackupTargetService.BundleExtension}";
        try
        {
            var storage = _storageFactory.Create(target);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            await using var dst = await storage.OpenWriteAsync(manifestName, ct);
            await dst.WriteAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Bundle manifest write for {Project} failed.", projectName);
            return (false, $"Volumes backed up, but the manifest could not be written: {ex.Message}");
        }

        var summary = $"Bundle '{manifestName}': {ok}/{ok + fail} volume(s), compose ({composeSource}), {services.Count} service(s).";
        return (fail == 0, summary);
    }

    /// <summary>Restores a bundle: re-creates its volumes, then recreates the stack from the compose and deploys it.</summary>
    public async Task<(bool Ok, string Message)> RestoreBundleAsync(BackupTarget? target, string manifestFileName, DockerEnvironment targetEnv, string? overrideName, bool overwriteVolumes, CancellationToken ct = default)
    {
        BundleManifest? manifest;
        try
        {
            var storage = _storageFactory.Create(target);
            await using var src = await storage.OpenReadAsync(Path.GetFileName(manifestFileName), ct);
            manifest = await JsonSerializer.DeserializeAsync<BundleManifest>(src, Json, ct);
        }
        catch (Exception ex)
        {
            return (false, $"Bundle manifest could not be read: {ex.Message}");
        }

        if (manifest is null)
        {
            return (false, "Invalid bundle manifest.");
        }

        int ok = 0, fail = 0;
        foreach (var v in manifest.Volumes)
        {
            if (string.IsNullOrEmpty(v.File))
            {
                fail++;
                continue;
            }

            var res = await _volumeBackupService.RestoreFromFileAsync(target, v.File, targetEnv, v.Name, overwriteVolumes, stopContainers: false, ct);
            if (res.Success) { ok++; } else { fail++; }
        }

        var name = string.IsNullOrWhiteSpace(overrideName) ? manifest.Name : overrideName.Trim();
        string deployMsg;
        if (!string.IsNullOrWhiteSpace(manifest.Compose))
        {
            var input = new StackInput { Name = name, EnvironmentId = targetEnv.Id, ComposeYaml = manifest.Compose };
            var existing = (await _stackService.GetAllAsync(ct))
                .FirstOrDefault(s => s.EnvironmentId == targetEnv.Id && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

            long stackId;
            if (existing is null)
            {
                var (cok, cmsg, cid) = await _stackService.CreateAsync(input, ct);
                if (!cok)
                {
                    return (false, $"Volumes restored ({ok}/{ok + fail}), but the stack could not be created: {cmsg}");
                }
                stackId = cid;
            }
            else
            {
                await _stackService.UpdateAsync(existing.Id, input, ct);
                stackId = existing.Id;
            }

            var (dok, dout) = await _stackService.DeployAsync(stackId, ct);
            deployMsg = dok ? "stack deployed (images pulled)" : $"deploy failed: {FirstLine(dout)}";
        }
        else
        {
            deployMsg = "no compose in bundle — only volumes restored";
        }

        return (fail == 0, $"Restored {ok}/{ok + fail} volume(s); {deployMsg}.");
    }

    private static string GenerateCompose(
        List<BundleService> services,
        List<(string Service, string Volume, string Dest)> serviceVolumes,
        IReadOnlyList<DockerContainer> containers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Best-effort compose reconstructed by MatDock from the running stack.");
        sb.AppendLine("# Review environment variables, networks and secrets before relying on it.");
        sb.AppendLine("services:");
        foreach (var s in services)
        {
            sb.AppendLine($"  {s.Service}:");
            sb.AppendLine($"    image: {s.Image}");
            sb.AppendLine($"    container_name: {s.ContainerName}");
            sb.AppendLine("    restart: unless-stopped");

            var c = containers.FirstOrDefault(x => x.Name == s.ContainerName);
            var ports = ParsePorts(c?.Ports);
            if (ports.Count > 0)
            {
                sb.AppendLine("    ports:");
                foreach (var p in ports)
                {
                    sb.AppendLine($"      - \"{p}\"");
                }
            }

            var vols = serviceVolumes.Where(v => v.Service == s.Service).ToList();
            if (vols.Count > 0)
            {
                sb.AppendLine("    volumes:");
                foreach (var v in vols)
                {
                    sb.AppendLine($"      - \"{v.Volume}:{v.Dest}\"");
                }
            }
        }

        var distinct = serviceVolumes.Select(v => v.Volume).Distinct().ToList();
        if (distinct.Count > 0)
        {
            // Reference the existing (restored) volumes by their exact names so no data is re-created.
            sb.AppendLine("volumes:");
            foreach (var v in distinct)
            {
                sb.AppendLine($"  {v}:");
                sb.AppendLine("    external: true");
                sb.AppendLine($"    name: {v}");
            }
        }

        return sb.ToString();
    }

    private static List<string> ParsePorts(string? ports)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(ports))
        {
            return result;
        }

        foreach (var seg in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = seg.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue; // not published to the host
            }

            var left = seg[..arrow];                 // e.g. 0.0.0.0:8080 or :::8080
            var right = seg[(arrow + 2)..];          // e.g. 80/tcp
            var hostPort = left.Contains(':') ? left[(left.LastIndexOf(':') + 1)..] : left;
            var containerPort = right.Split('/')[0];
            if (hostPort.Length > 0 && containerPort.Length > 0)
            {
                var entry = $"{hostPort}:{containerPort}";
                if (!result.Contains(entry))
                {
                    result.Add(entry);
                }
            }
        }

        return result;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "unknown error";
        }

        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0)
            {
                return t;
            }
        }

        return "unknown error";
    }
}
