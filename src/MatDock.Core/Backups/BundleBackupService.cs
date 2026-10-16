using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using MatDock.Core.Containers;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Stacks;
using MatDock.Core.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Backups;

/// <summary>Outcome of a stack bundle backup: success, a summary and the id of its <see cref="StackBackup"/> record
/// (null when the request was rejected before a record existed).</summary>
public sealed record StackBackupResult(bool Ok, string Message, long? BackupId);

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

    /// <summary>The <c>.env</c> of a managed inline stack ("" = it had none); null when unknown (generated compose,
    /// Git stack, or a bundle from an older MatDock) — a restore then keeps the stack's current <c>.env</c>.</summary>
    public string? EnvContent { get; set; }

    /// <summary>Owner of the managed stack (e.g. <c>mattiverse</c>), restored with a re-created stack.</summary>
    public string? ManagedBy { get; set; }

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
/// Full backups of a whole stack/app: its volumes (as tar archives), the compose definition, its <c>.env</c> and the
/// image references (images are pulled again on restore — <c>docker compose up</c> handles that). Every bundle is
/// recorded as a <see cref="StackBackup"/>. A restore re-creates the volumes, then recreates the stack from the
/// compose and deploys it.
/// </summary>
public sealed class BundleBackupService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>A bundle still marked Running after this long was interrupted (e.g. by a restart) and may be deleted.</summary>
    private static readonly TimeSpan StaleRunning = TimeSpan.FromHours(6);

    private readonly MatDockDbContext _db;
    private readonly ContainerService _containerService;
    private readonly StackService _stackService;
    private readonly VolumeBackupService _volumeBackupService;
    private readonly IBackupStorageFactory _storageFactory;
    private readonly ILogger<BundleBackupService> _logger;

    public BundleBackupService(
        MatDockDbContext db,
        ContainerService containerService,
        StackService stackService,
        VolumeBackupService volumeBackupService,
        IBackupStorageFactory storageFactory,
        ILogger<BundleBackupService> logger)
    {
        _db = db;
        _containerService = containerService;
        _stackService = stackService;
        _volumeBackupService = volumeBackupService;
        _storageFactory = storageFactory;
        _logger = logger;
    }

    /// <summary>Backs up a whole stack (all its containers' volumes + compose + image refs) to a target.</summary>
    public async Task<(bool Ok, string Message)> BackupStackAsync(DockerEnvironment env, string projectName, BackupTarget? target, bool stopContainers, CancellationToken ct = default)
    {
        var result = await CreateBundleAsync(env, projectName, target, stopContainers, ct: ct);
        return (result.Ok, result.Message);
    }

    /// <summary>
    /// Backs up a whole stack and records it as a <see cref="StackBackup"/> (status Running while it runs, then
    /// Succeeded or Failed). <paramref name="scheduledTaskId"/> links a scheduled bundle to its task for retention.
    /// </summary>
    public async Task<StackBackupResult> CreateBundleAsync(DockerEnvironment env, string projectName, BackupTarget? target, bool stopContainers,
        StackBackupKind kind = StackBackupKind.Manual, string? note = null, long? scheduledTaskId = null, CancellationToken ct = default)
    {
        if (!ContainerCommands.IsValidId(projectName))
        {
            return new StackBackupResult(false, "Invalid stack/project name.", null);
        }

        var record = new StackBackup
        {
            EnvironmentId = env.Id,
            EnvironmentName = env.Name,
            StackName = projectName,
            Kind = kind,
            Status = StackBackupStatus.Running,
            BackupTargetId = target?.Id,
            BackupTargetName = target?.Name ?? "Local",
            Consistent = stopContainers,
            Note = string.IsNullOrWhiteSpace(note) ? null : Truncate(note.Trim(), 500),
            ScheduledTaskId = scheduledTaskId,
        };
        _db.StackBackups.Add(record);
        await _db.SaveChangesAsync(ct);

        bool ok;
        string message;
        try
        {
            (ok, message) = await RunBundleAsync(record, env, projectName, target, stopContainers, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Bundle backup of {Project} failed.", projectName);
            (ok, message) = (false, $"Error: {ex.Message}");
        }

        record.Status = ok ? StackBackupStatus.Succeeded : StackBackupStatus.Failed;
        record.Message = Truncate(message, 1000);
        try
        {
            // Persist the outcome even when the caller was cancelled meanwhile.
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the outcome of bundle {Id} failed.", record.Id);
        }

        return new StackBackupResult(ok, message, record.Id);
    }

    private async Task<(bool Ok, string Message)> RunBundleAsync(StackBackup record, DockerEnvironment env, string projectName, BackupTarget? target, bool stopContainers, CancellationToken ct)
    {
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

        // Prefer the real stored compose (and .env) of a managed inline stack; otherwise reconstruct a best-effort one.
        var stack = (await _stackService.GetAllAsync(ct)).FirstOrDefault(s =>
            s.EnvironmentId == env.Id && string.Equals(s.Name, projectName, StringComparison.OrdinalIgnoreCase));
        var managed = stack is { IsGitBacked: false } && !string.IsNullOrWhiteSpace(stack.ComposeYaml) ? stack : null;

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
            var res = await _volumeBackupService.BackupAsync(env, vol, target, scheduleId: null, ct: ct, stopContainers: stopContainers,
                stackBackupId: record.Id);
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
            EnvContent = managed is null ? null : managed.EnvContent ?? string.Empty,
            ManagedBy = stack?.ManagedBy,
            Services = services,
            Volumes = volumes
        };

        var manifestName = $"{projectName}_{ts:yyyyMMddHHmmss}{BackupTargetService.BundleExtension}";
        long manifestBytes;
        try
        {
            var storage = _storageFactory.Create(target);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            await using var dst = await storage.OpenWriteAsync(manifestName, ct);
            await dst.WriteAsync(bytes, ct);
            manifestBytes = bytes.Length;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Bundle manifest write for {Project} failed.", projectName);
            return (false, $"Volumes backed up, but the manifest could not be written: {ex.Message}");
        }

        record.ManifestFileName = manifestName;
        record.VolumeCount = ok;
        record.SizeBytes = volumes.Sum(v => v.SizeBytes) + manifestBytes;

        var envNote = manifest.EnvContent is { Length: > 0 } ? " + .env" : string.Empty;
        var summary = $"Bundle '{manifestName}': {ok}/{ok + fail} volume(s), compose ({composeSource}){envNote}, {services.Count} service(s).";
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
        var deployOk = true;
        string deployMsg;
        if (!string.IsNullOrWhiteSpace(manifest.Compose))
        {
            var existing = (await _stackService.GetAllAsync(ct))
                .FirstOrDefault(s => s.EnvironmentId == targetEnv.Id && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            var input = new StackInput
            {
                Name = name,
                EnvironmentId = targetEnv.Id,
                ComposeYaml = manifest.Compose,
                // The bundle's .env when it carries one; an older bundle (none) keeps the stack's current .env.
                EnvContent = manifest.EnvContent ?? existing?.EnvContent,
                // A re-created stack gets its owner back; an existing one keeps its own (null = keep).
                ManagedBy = existing is null ? manifest.ManagedBy : null,
            };

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
            deployOk = dok;
            deployMsg = dok ? "stack deployed (images pulled)" : $"deploy failed: {FirstLine(dout)}";
        }
        else
        {
            deployMsg = "no compose in bundle — only volumes restored";
        }

        return (fail == 0 && deployOk, $"Restored {ok}/{ok + fail} volume(s); {deployMsg}.");
    }

    // ---- Recorded bundles (StackBackup) -------------------------------------------------------

    /// <summary>Recorded bundles, newest first, optionally of one environment and/or stack.</summary>
    public Task<List<StackBackup>> GetBundlesAsync(long? environmentId = null, string? stackName = null, CancellationToken ct = default)
    {
        var query = _db.StackBackups.AsNoTracking();
        if (environmentId is { } envId)
        {
            query = query.Where(b => b.EnvironmentId == envId);
        }

        if (!string.IsNullOrWhiteSpace(stackName))
        {
            query = query.Where(b => b.StackName == stackName);
        }

        return query.OrderByDescending(b => b.CreateDate).ThenByDescending(b => b.Id).ToListAsync(ct);
    }

    public Task<StackBackup?> GetBundleAsync(long id, CancellationToken ct = default)
        => _db.StackBackups.FirstOrDefaultAsync(b => b.Id == id, ct);

    /// <summary>
    /// Restores a recorded bundle into its environment: optionally backs up the current state first (abort when that
    /// fails), stops the running containers of the stack, restores the volumes (overwriting them), writes the compose
    /// and <c>.env</c> back (re-creating the stack when it is gone) and deploys it.
    /// </summary>
    public async Task<(bool Ok, string Message, long? SafetyBackupId)> RestoreAsync(long id, DockerEnvironment env, bool backupCurrentFirst,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var record = await GetBundleAsync(id, ct);
        if (record is null)
        {
            return (false, "Backup not found.", null);
        }

        if (record.Status != StackBackupStatus.Succeeded || string.IsNullOrEmpty(record.ManifestFileName))
        {
            return (false, "Only a completed backup can be restored.", null);
        }

        var (targetOk, target) = await ResolveTargetAsync(record.BackupTargetId, ct);
        if (!targetOk)
        {
            return (false, "The backup target of this backup has been deleted - restore is not possible.", null);
        }

        var containers = await _containerService.ListByProjectAsync(env, record.StackName, ct);
        long? safetyId = null;
        if (backupCurrentFirst && containers.Count > 0)
        {
            log?.Invoke("==> Backing up the current state first");
            var safety = await CreateBundleAsync(env, record.StackName, target, stopContainers: false,
                StackBackupKind.Manual, $"Before restoring backup #{record.Id}", ct: ct);
            safetyId = safety.BackupId;
            log?.Invoke(safety.Message);
            if (!safety.Ok)
            {
                return (false, $"The backup of the current state failed, restore aborted: {safety.Message}", safetyId);
            }
        }

        if (containers.Any(c => c.IsRunning))
        {
            log?.Invoke("==> Stopping the stack");
            var (stopped, stopOutput) = await _stackService.ProjectStopAsync(env, record.StackName, ct);
            if (!stopped)
            {
                return (false, $"The stack could not be stopped, restore aborted: {FirstLine(stopOutput)}", safetyId);
            }
        }

        log?.Invoke($"==> Restoring volumes and compose from {record.ManifestFileName}");
        var (ok, message) = await RestoreBundleAsync(target, record.ManifestFileName, env, overrideName: record.StackName,
            overwriteVolumes: true, ct);
        return (ok, message, safetyId);
    }

    /// <summary>Deletes a recorded bundle: its volume archives, its manifest and the record. A bundle that is still
    /// running cannot be deleted (unless it was interrupted long ago).</summary>
    public async Task<(bool Found, string? Error)> DeleteBundleAsync(long id, CancellationToken ct = default)
    {
        var record = await GetBundleAsync(id, ct);
        if (record is null)
        {
            return (false, null);
        }

        if (record.Status == StackBackupStatus.Running && DateTime.UtcNow - record.CreateDate < StaleRunning)
        {
            return (true, "The backup is still running.");
        }

        var volumeIds = await _db.VolumeBackups.Where(v => v.StackBackupId == id).Select(v => v.Id).ToListAsync(ct);
        foreach (var volumeId in volumeIds)
        {
            await _volumeBackupService.DeleteAsync(volumeId, ct);
        }

        if (!string.IsNullOrEmpty(record.ManifestFileName))
        {
            var (_, target) = await ResolveTargetAsync(record.BackupTargetId, ct);
            try
            {
                await _storageFactory.Create(target).DeleteAsync(record.ManifestFileName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Deleting the manifest of bundle {Id} failed.", id);
            }
        }

        _db.StackBackups.Remove(record); // soft delete
        await _db.SaveChangesAsync(ct);
        return (true, null);
    }

    /// <summary>
    /// Writes a recorded bundle as one tar archive to <paramref name="output"/>: the manifest followed by the volume
    /// archives (each a tar itself), under their file names at the target.
    /// </summary>
    public async Task WriteBundleTarAsync(StackBackup record, Stream output, CancellationToken ct = default)
    {
        var (_, target) = await ResolveTargetAsync(record.BackupTargetId, ct);
        var storage = _storageFactory.Create(target);
        var files = new List<string> { record.ManifestFileName! };
        files.AddRange(await _db.VolumeBackups.AsNoTracking()
            .Where(v => v.StackBackupId == record.Id)
            .OrderBy(v => v.Id)
            .Select(v => v.FileName)
            .ToListAsync(ct));

        await using var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var file in files)
        {
            await using var source = await storage.OpenReadAsync(file, ct);
            string? temp = null;
            try
            {
                // The tar header needs the size up front: spool a non-seekable source (SMB) to a temp file first.
                var data = source;
                if (!source.CanSeek)
                {
                    temp = Path.GetTempFileName();
                    await using (var spool = File.Create(temp))
                    {
                        await source.CopyToAsync(spool, ct);
                    }

                    data = File.OpenRead(temp);
                }

                await using (data == source ? null : data)
                {
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(file))
                    {
                        DataStream = data,
                        ModificationTime = DateTime.SpecifyKind(record.CreateDate, DateTimeKind.Utc),
                    };
                    await tar.WriteEntryAsync(entry, ct);
                }
            }
            finally
            {
                if (temp is not null)
                {
                    try { File.Delete(temp); } catch { /* best effort */ }
                }
            }
        }
    }

    /// <summary>
    /// Retention of a bundle schedule for one stack: keeps the newest <paramref name="keepCount"/> succeeded bundles
    /// of this task (0 = unlimited), drops those older than <paramref name="keepDays"/> days (0 = off) and failed
    /// attempts that a newer successful bundle superseded. Returns how many bundles were deleted.
    /// </summary>
    public async Task<int> ApplyRetentionAsync(long scheduledTaskId, long environmentId, string stackName, int keepCount, int keepDays, CancellationToken ct = default)
    {
        var bundles = await _db.StackBackups.AsNoTracking()
            .Where(b => b.ScheduledTaskId == scheduledTaskId && b.EnvironmentId == environmentId && b.StackName == stackName
                        && b.Kind == StackBackupKind.Scheduled && b.Status != StackBackupStatus.Running)
            .ToListAsync(ct);

        var doomed = SelectForRetention(bundles, keepCount, keepDays, DateTime.UtcNow);
        foreach (var id in doomed)
        {
            await DeleteBundleAsync(id, ct);
        }

        return doomed.Count;
    }

    /// <summary>The ids that retention removes (see <see cref="ApplyRetentionAsync"/>); pure, for tests.</summary>
    public static IReadOnlyList<long> SelectForRetention(IEnumerable<StackBackup> bundles, int keepCount, int keepDays, DateTime nowUtc)
    {
        var ordered = bundles.OrderByDescending(b => b.CreateDate).ThenByDescending(b => b.Id).ToList();
        var succeeded = ordered.Where(b => b.Status == StackBackupStatus.Succeeded).ToList();
        var doomed = new HashSet<long>();

        if (keepCount > 0)
        {
            foreach (var old in succeeded.Skip(keepCount)) { doomed.Add(old.Id); }
        }

        if (keepDays > 0)
        {
            var cutoff = nowUtc.AddDays(-keepDays);
            foreach (var old in ordered.Where(b => b.CreateDate < cutoff)) { doomed.Add(old.Id); }
        }

        if (succeeded.FirstOrDefault() is { } newest)
        {
            foreach (var failed in ordered.Where(b => b.Status == StackBackupStatus.Failed && b.CreateDate < newest.CreateDate))
            {
                doomed.Add(failed.Id);
            }
        }

        return ordered.Where(b => doomed.Contains(b.Id)).Select(b => b.Id).ToList();
    }

    /// <summary>The target of a backup by id, even if it was soft-deleted; (false, null) when it no longer exists.</summary>
    private async Task<(bool Ok, BackupTarget? Target)> ResolveTargetAsync(long? targetId, CancellationToken ct)
    {
        if (targetId is not { } tid)
        {
            return (true, null);
        }

        var target = await _db.BackupTargets.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tid, ct);
        return (target is not null, target);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

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
