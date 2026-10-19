using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using MatDock.Core.Apps;
using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Git;
using MatDock.Core.Execution;
using MatDock.Core.Registries;
using MatDock.Core.Schedules;
using MatDock.Core.Ssh;
using MatDock.Core.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatDock.Core.Stacks;

/// <summary>CRUD for managed stacks plus deploy/down via <c>docker compose</c> over SSH. A stack is either
/// inline (compose YAML from the editor) or git-backed (repo cloned and the whole tree shipped to the host).</summary>
public sealed class StackService
{
    private readonly MatDockDbContext _db;
    private readonly IHostSessionFactory _hostSessionFactory;
    private readonly EnvironmentService _environmentService;
    private readonly GitCredentialService _gitCredentials;
    private readonly GitRepositoryService _gitRepo;
    private readonly RegistryService _registries;
    private readonly IScheduleEventBus _events;
    private readonly MatDockOptions _options;
    private readonly ILogger<StackService> _logger;

    public StackService(
        MatDockDbContext db,
        IHostSessionFactory hostSessionFactory,
        EnvironmentService environmentService,
        GitCredentialService gitCredentials,
        GitRepositoryService gitRepo,
        RegistryService registries,
        IScheduleEventBus events,
        IOptions<MatDockOptions> options,
        ILogger<StackService> logger)
    {
        _db = db;
        _hostSessionFactory = hostSessionFactory;
        _environmentService = environmentService;
        _gitCredentials = gitCredentials;
        _gitRepo = gitRepo;
        _registries = registries;
        _events = events;
        _options = options.Value;
        _logger = logger;
    }

    public Task<List<Stack>> GetAllAsync(CancellationToken ct = default)
        => _db.Stacks.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);

    public Task<Stack?> GetAsync(long id, CancellationToken ct = default)
        => _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);

    /// <summary>The managed stack with this (compose project) name in an environment, or null.</summary>
    public Task<Stack?> GetByNameAsync(long environmentId, string name, CancellationToken ct = default)
        => _db.Stacks.FirstOrDefaultAsync(s => s.EnvironmentId == environmentId && s.Name == name, ct);

    /// <summary>
    /// Creates or updates an inline stack by (environment, name) — the same validation and storage as the editor.
    /// Repeating it with the same values changes nothing. <paramref name="managedBy"/> null keeps the stored owner.
    /// Fails for a git-backed stack of that name (its compose comes from the repository).
    /// </summary>
    public async Task<(bool Ok, string Message, Stack? Stack, bool Created)> UpsertInlineAsync(
        long environmentId, string name, string composeYaml, string? envContent, string? managedBy, CancellationToken ct = default)
    {
        var input = new StackInput
        {
            Name = name,
            EnvironmentId = environmentId,
            ComposeYaml = composeYaml,
            EnvContent = envContent,
            ManagedBy = managedBy,
        };

        var existing = await GetByNameAsync(environmentId, name.Trim(), ct);
        if (existing is null)
        {
            var (created, message, id) = await CreateAsync(input, ct);
            return (created, message, created ? await GetAsync(id, ct) : null, true);
        }

        if (existing.IsGitBacked)
        {
            return (false, "This stack is deployed from a Git repository; its compose file cannot be replaced.", existing, false);
        }

        var (ok, msg) = await UpdateAsync(existing.Id, input, ct);
        return (ok, msg, existing, false);
    }

    public async Task<(bool Ok, string Message, long Id)> CreateAsync(StackInput input, CancellationToken ct = default)
    {
        if (!StackCommands.IsValidName(input.Name))
        {
            return (false, "Invalid stack name (lowercase, a-z 0-9 _ -, must start alphanumeric).", 0);
        }

        var gitError = ValidateGit(input);
        if (gitError is not null)
        {
            return (false, gitError, 0);
        }

        var name = input.Name.Trim();
        if (await _db.Stacks.AnyAsync(s => s.Name == name && s.EnvironmentId == input.EnvironmentId, ct))
        {
            return (false, "A stack with this name already exists in this environment.", 0);
        }

        var stack = new Stack();
        Apply(stack, input);
        _db.Stacks.Add(stack);
        await _db.SaveChangesAsync(ct);
        return (true, "Stack created.", stack.Id);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(long id, StackInput input, CancellationToken ct = default)
    {
        if (!StackCommands.IsValidName(input.Name))
        {
            return (false, "Invalid stack name (lowercase, a-z 0-9 _ -, must start alphanumeric).");
        }

        var gitError = ValidateGit(input);
        if (gitError is not null)
        {
            return (false, gitError);
        }

        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return (false, "Stack not found.");
        }

        var name = input.Name.Trim();
        if (await _db.Stacks.AnyAsync(s => s.Id != id && s.Name == name && s.EnvironmentId == input.EnvironmentId, ct))
        {
            return (false, "A stack with this name already exists in this environment.");
        }

        Apply(stack, input);
        await _db.SaveChangesAsync(ct);
        return (true, "Stack saved.");
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return false;
        }

        _db.Stacks.Remove(stack); // soft delete
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool Ok, string Output)> DeployAsync(long id, CancellationToken ct = default)
    {
        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return (false, "Stack not found.");
        }

        return stack.IsGitBacked ? await GitDeployAsync(stack, pull: false, ct) : await InlineDeployAsync(stack, pull: false, ct);
    }

    /// <summary>Pulls newer images and redeploys the stack (manual update). Same paths as deploy, but with a pull.</summary>
    public async Task<(bool Ok, string Output)> UpdateAsync(long id, CancellationToken ct = default)
    {
        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return (false, "Stack not found.");
        }

        return stack.IsGitBacked ? await GitDeployAsync(stack, pull: true, ct) : await InlineDeployAsync(stack, pull: true, ct);
    }

    public Task<(bool Ok, string Output)> DownAsync(long id, CancellationToken ct = default)
        => DownAsync(id, removeVolumes: false, ct);

    /// <summary><c>compose down</c>: stops and removes the stack's containers (and with <paramref name="removeVolumes"/>
    /// its named volumes). The stack definition stays in MatDock.</summary>
    public Task<(bool Ok, string Output)> DownAsync(long id, bool removeVolumes, CancellationToken ct = default)
        => LifecycleAsync(id, (head, name, path) => StackCommands.Down(head, name, path, removeVolumes), null, null, ct);

    /// <summary><c>compose restart</c>: restarts the stack's containers without re-creating them.</summary>
    public Task<(bool Ok, string Output)> RestartAsync(long id, CancellationToken ct = default)
        => LifecycleAsync(id, StackCommands.Restart, "Restarted", "Restart failed", ct);

    /// <summary><c>compose pull</c>: fetches newer images; the running containers are not touched.</summary>
    public Task<(bool Ok, string Output)> PullAsync(long id, CancellationToken ct = default)
        => LifecycleAsync(id, StackCommands.Pull, "Pulled", "Pull failed", ct);

    private async Task<(bool Ok, string Output)> LifecycleAsync(
        long id, Func<string, string, string, string> build, string? successStatus, string? failureStatus, CancellationToken ct)
    {
        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return (false, "Stack not found.");
        }

        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        var command = build(prep.Head!, stack.Name, EffectiveComposePath(stack));
        return await ExecuteAndPersistAsync(stack, deploy: false, prep, command, writeStdin: null, ct,
            successStatus: successStatus, failureStatus: failureStatus);
    }

    /// <summary><c>compose stop</c>: stops the stack's containers but keeps them (fast restart).</summary>
    public async Task<(bool Ok, string Output)> StopAsync(long id, CancellationToken ct = default)
    {
        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return (false, "Stack not found.");
        }

        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        var command = StackCommands.Stop(prep.Head!, stack.Name, EffectiveComposePath(stack));
        return await ExecuteAndPersistAsync(stack, deploy: false, prep, command, writeStdin: null, ct,
            successStatus: "Stopped", failureStatus: "Stop failed");
    }

    /// <summary><c>compose start</c>: starts the stack's previously-stopped containers.</summary>
    public async Task<(bool Ok, string Output)> StartAsync(long id, CancellationToken ct = default)
    {
        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return (false, "Stack not found.");
        }

        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        var command = StackCommands.Start(prep.Head!, stack.Name, EffectiveComposePath(stack));
        return await ExecuteAndPersistAsync(stack, deploy: false, prep, command, writeStdin: null, ct,
            successStatus: "Started", failureStatus: "Start failed");
    }

    // ---- Discovered (external) stacks: lifecycle by compose project label (no managed record) ----

    public Task<(bool Ok, string Output)> ProjectStartAsync(DockerEnvironment env, string project, CancellationToken ct = default)
        => ProjectActionAsync(env, project, StackCommands.ProjectStart, ct);

    public Task<(bool Ok, string Output)> ProjectStopAsync(DockerEnvironment env, string project, CancellationToken ct = default)
        => ProjectActionAsync(env, project, StackCommands.ProjectStop, ct);

    public Task<(bool Ok, string Output)> ProjectRestartAsync(DockerEnvironment env, string project, CancellationToken ct = default)
        => ProjectActionAsync(env, project, StackCommands.ProjectRestart, ct);

    /// <summary>
    /// Adopts a discovered (external) compose project into MatDock management: reads the project's compose
    /// file from the host (via its compose labels) and creates a managed <see cref="Stack"/> with that YAML.
    /// Does NOT touch the running containers — it only creates the definition so the stack becomes editable.
    /// </summary>
    public async Task<(bool Ok, string Message)> AdoptAsync(DockerEnvironment env, string project, CancellationToken ct = default)
    {
        if (!StackCommands.IsValidName(project))
        {
            return (false, "Invalid project name.");
        }
        if (!env.IsEnabled)
        {
            return (false, "Environment not available (disabled).");
        }
        if (await _db.Stacks.AnyAsync(s => s.Name == project && s.EnvironmentId == env.Id, ct))
        {
            return (false, "A managed stack with this name already exists in this environment.");
        }

        var settings = _environmentService.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var timeout = TimeSpan.FromSeconds(Math.Max(60, _options.SshTimeoutSeconds));

        string labelsJson;
        try
        {
            var (ok, output) = await ExecOnHostAsync(settings, StackCommands.AdoptInspect(head, project), null, timeout, ct, stream: false);
            if (!ok || string.IsNullOrWhiteSpace(output))
            {
                return (false, "Could not find a container for this project to read its compose file.");
            }
            labelsJson = output.Trim();
        }
        catch (Exception ex)
        {
            return (false, DockerErrorMessages.IsSshError(ex) ? DockerErrorMessages.DescribeSshError(ex) : $"Error: {ex.Message}");
        }

        string? configFiles = null, workingDir = null;
        try
        {
            using var doc = JsonDocument.Parse(labelsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("com.docker.compose.project.config_files", out var cf))
                {
                    configFiles = cf.GetString();
                }
                if (doc.RootElement.TryGetProperty("com.docker.compose.project.working_dir", out var wd))
                {
                    workingDir = wd.GetString();
                }
            }
        }
        catch
        {
            // fall through to the empty check below
        }

        if (string.IsNullOrWhiteSpace(configFiles))
        {
            return (false, "This project has no compose-file label, so it can't be imported automatically. Create it as a new stack instead.");
        }

        if (AdoptPath(configFiles, workingDir) is not { } target)
        {
            return (false, "The compose file named by the project's labels is not a .yml/.yaml file in a project folder, so it is not read.");
        }

        var (path, dir, fileName) = target;

        string yaml;
        try
        {
            var (ok, output) = await ExecOnHostAsync(settings, StackCommands.ReadProjectFile(head, dir, fileName, _options.HelperImage), null, timeout, ct, stream: false);
            if (!ok || string.IsNullOrWhiteSpace(output))
            {
                return (false, $"Could not read the compose file ({path}) from the host.");
            }
            yaml = output;
        }
        catch (Exception ex)
        {
            return (false, DockerErrorMessages.IsSshError(ex) ? DockerErrorMessages.DescribeSshError(ex) : $"Error: {ex.Message}");
        }

        var (created, message, _) = await CreateAsync(new StackInput
        {
            Name = project,
            EnvironmentId = env.Id,
            ComposeYaml = yaml,
        }, ct);
        return (created, created ? $"Imported '{project}' as a managed stack. Review it before deploying." : message);
    }

    private static bool IsAbsoluteHostPath(string p)
        => p.StartsWith('/') || (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':');

    private async Task<(bool Ok, string Output)> ProjectActionAsync(DockerEnvironment env, string project, Func<string, string, string> build, CancellationToken ct)
    {
        if (!StackCommands.IsValidName(project))
        {
            return (false, "Invalid project name.");
        }
        if (!env.IsEnabled)
        {
            return (false, "Environment not available (disabled).");
        }

        var settings = _environmentService.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var timeout = TimeSpan.FromSeconds(Math.Max(60, _options.MigrationTimeoutSeconds));
        try
        {
            return await ExecOnHostAsync(settings, build(head, project), writeStdin: null, timeout, ct);
        }
        catch (Exception ex)
        {
            return (false, DockerErrorMessages.IsSshError(ex) ? DockerErrorMessages.DescribeSshError(ex) : $"Error: {ex.Message}");
        }
    }

    private async Task<(bool Ok, string Output)> InlineDeployAsync(Stack stack, bool pull, CancellationToken ct)
    {
        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        await LoginRegistriesAsync(prep, ct);

        var yaml = StackCommands.FilesOnStdin(stack.ComposeYaml, stack.EnvContent);
        var command = pull
            ? StackCommands.Update(prep.Head!, stack.Name)
            : StackCommands.Deploy(prep.Head!, stack.Name);
        return await ExecuteAndPersistAsync(stack, deploy: true, prep, command,
            (s, c) => s.WriteAsync(yaml, c).AsTask(), ct,
            beforePersist: s => s.AppMetaJson = BuildAppMetaJson(s.ComposeYaml, null, null));
    }

    private async Task<(bool Ok, string Output)> GitDeployAsync(Stack stack, bool pull, CancellationToken ct)
    {
        // Early exit before cloning if the environment is unavailable.
        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        GitCredentialSecret? secret = null;
        if (stack.GitCredentialId is > 0)
        {
            try
            {
                secret = await _gitCredentials.ResolveAsync(stack.GitCredentialId.Value, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stack {Name}: git credential decrypt failed.", stack.Name);
                return (false, "Git credentials could not be decrypted (Data Protection key?).");
            }

            if (secret is null)
            {
                return (false, "The assigned Git credentials were not found.");
            }
        }

        string? workDir = null;
        try
        {
            workDir = await _gitRepo.CloneToTempAsync(stack.GitRepoUrl!, stack.GitReference, secret, ct);
            return await DeployFromWorkDirAsync(stack, workDir, pull, ct);
        }
        catch (GitOperationException ex)
        {
            stack.LastStatus = "Git error";
            try { await _db.SaveChangesAsync(ct); } catch { /* best effort */ }
            return (false, ex.Message);
        }
        finally
        {
            if (workDir is not null)
            {
                GitRepositoryService.TryDelete(workDir);
            }
        }
    }

    /// <summary>
    /// Deploys a git-backed stack from an ALREADY-cloned working directory (skips the clone). Used by
    /// sync jobs that clone a repo once and deploy many compose files from the same tree. The caller
    /// owns <paramref name="workDir"/> and is responsible for deleting it.
    /// </summary>
    public async Task<(bool Ok, string Output)> DeployFromWorkDirAsync(Stack stack, string workDir, bool pull, CancellationToken ct = default)
    {
        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        var composePath = EffectiveComposePath(stack);
        var composeFull = RepoFile(workDir, Path.Combine(workDir, composePath.Replace('/', Path.DirectorySeparatorChar)));
        if (composeFull is null || !File.Exists(composeFull))
        {
            stack.LastStatus = "Compose file missing";
            try { await _db.SaveChangesAsync(ct); } catch { /* best effort */ }
            return (false, $"Compose file not found in the repo: {composePath}");
        }

        var composeContent = await File.ReadAllTextAsync(composeFull, ct);

        await LoginRegistriesAsync(prep, ct);

        // Stream the tar straight into the SSH channel (no full-repo buffer in memory).
        var localDir = workDir;
        var command = StackCommands.GitSync(prep.Head!, stack.Name, composePath, pull);
        return await ExecuteAndPersistAsync(stack, deploy: true, prep, command,
            (s, _) => { TarFile.CreateFromDirectory(localDir, s, includeBaseDirectory: false); return Task.CompletedTask; },
            ct, beforePersist: s =>
            {
                s.ComposeYaml = composeContent;
                s.AppMetaJson = BuildAppMetaJson(composeContent, localDir, composePath);
            });
    }

    /// <summary>
    /// Resolves MatDock app metadata for a stack: the compose <c>x-matdock:</c> block, overlaid by a
    /// <c>matdock.yml|yaml|json</c> sidecar next to the compose (git only), with a file icon resolved to a
    /// data URL. Returns JSON for storage, or null when there is no app metadata. Never throws.
    /// </summary>
    private string? BuildAppMetaJson(string composeYaml, string? workDir, string? composePath)
    {
        AppMetadata? meta;
        try
        {
            meta = AppMetadataParser.FromCompose(composeYaml);

            if (workDir is not null && composePath is not null)
            {
                var composeFull = Path.Combine(workDir, composePath.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(composeFull) ?? workDir;

                foreach (var sidecar in new[] { "matdock.yml", "matdock.yaml", "matdock.json" })
                {
                    var path = RepoFile(workDir, Path.Combine(dir, sidecar));
                    if (path is not null && File.Exists(path))
                    {
                        meta = AppMetadataParser.Merge(meta, AppMetadataParser.FromSidecar(File.ReadAllText(path), sidecar));
                        break;
                    }
                }

                meta = ResolveIconFile(meta, workDir, dir);
            }

            // An unresolved file reference (inline stack, or missing/oversized file) must not render as text.
            if (meta is not null && AppIcons.IsFileName(meta.Icon))
            {
                meta.Icon = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "App metadata parse failed.");
            return null;
        }

        return meta is null || meta.IsEmpty ? null : AppMetadataParser.ToJson(meta);
    }

    /// <summary>
    /// <paramref name="path"/> when it is a file inside the clone <paramref name="root"/> that is not reached through a symbolic
    /// link; null otherwise. A repository decides these paths (compose path, sidecar, icon), so a link or a <c>..</c> must not
    /// make MatDock read its own files (the keys, the database) into a stack.
    /// </summary>
    internal static string? RepoFile(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        for (var current = full; current.Length > fullRoot.Length; current = Path.GetDirectoryName(current) ?? fullRoot)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                return null;
            }
        }

        return full;
    }

    private static AppMetadata? ResolveIconFile(AppMetadata? meta, string root, string dir)
    {
        if (meta is null)
        {
            return null;
        }

        try
        {
            if (AppIcons.IsFileName(meta.Icon))
            {
                var path = RepoFile(root, Path.Combine(dir, meta.Icon!.Replace('/', Path.DirectorySeparatorChar)));
                meta.Icon = path is not null && File.Exists(path) ? AppIcons.ToDataUrl(File.ReadAllBytes(path), path) : null;
            }

            if (string.IsNullOrWhiteSpace(meta.Icon))
            {
                foreach (var name in AppIcons.DefaultFileNames)
                {
                    var path = RepoFile(root, Path.Combine(dir, name));
                    if (path is not null && File.Exists(path))
                    {
                        var url = AppIcons.ToDataUrl(File.ReadAllBytes(path), path);
                        if (url is not null)
                        {
                            meta.Icon = url;
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // best-effort icon resolution
        }

        return meta;
    }

    /// <summary>Lets compose on the environment check a compose file and its .env (nothing is deployed or kept). Output = compose's words.</summary>
    public async Task<(bool Ok, string Output)> CheckComposeAsync(long environmentId, string composeYaml, string? envContent, CancellationToken ct = default)
    {
        var env = await _environmentService.GetAsync(environmentId, ct);
        if (env is null || !env.IsEnabled)
        {
            return (false, "Environment not available (disabled or deleted).");
        }

        var settings = _environmentService.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        var files = StackCommands.FilesOnStdin(composeYaml ?? string.Empty, envContent);
        try
        {
            return await ExecOnHostAsync(settings, StackCommands.CheckConfig(head), (s, c) => s.WriteAsync(files, c).AsTask(),
                TimeSpan.FromSeconds(Math.Max(30, _options.SshTimeoutSeconds)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// The compose file an adopt reads, from the labels of the project's containers: the first of <c>config_files</c>, relative ones joined to
    /// <c>working_dir</c> (POSIX and Windows paths). The labels come from whoever started the containers, so only a <c>.yml</c>/<c>.yaml</c> file
    /// in a folder that is not a root of the file system, with no <c>..</c>, is read (its folder is mounted read-only to read it). Null otherwise.
    /// </summary>
    internal static (string Path, string Dir, string FileName)? AdoptPath(string configFiles, string? workingDir)
    {
        var first = configFiles.Split(',')[0].Trim();
        var path = IsAbsoluteHostPath(first) ? first : $"{(workingDir ?? ".").TrimEnd('/', '\\')}/{first}";
        var sep = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        if (sep <= 0)
        {
            return null;
        }

        var dir = path[..sep];
        var fileName = path[(sep + 1)..];
        var segments = path.Split('/', '\\');
        var isRoot = dir.Length == 0 || dir == "/" || (dir.Length <= 3 && dir.Length >= 2 && dir[1] == ':');
        if (!IsAbsoluteHostPath(dir + "/") || isRoot || segments.Any(x => x == "..")
            || !(fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return (path, dir, fileName);
    }

    private sealed record HostPrep(bool Ok, string Message, SshConnectionSettings? Settings, string? Head, TimeSpan Timeout);

    private async Task<HostPrep> PrepareHostAsync(Stack stack, CancellationToken ct)
    {
        var env = await _environmentService.GetAsync(stack.EnvironmentId, ct);
        if (env is null || !env.IsEnabled)
        {
            return new HostPrep(false, "Environment not available (disabled or deleted).", null, null, default);
        }

        var settings = _environmentService.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        // compose can pull images / a git tar can be sizeable -> allow a generous timeout.
        var timeout = TimeSpan.FromSeconds(Math.Max(120, _options.MigrationTimeoutSeconds));
        return new HostPrep(true, string.Empty, settings, head, timeout);
    }

    /// <summary>Runs a host command (optionally streaming <paramref name="stdin"/>) and persists the stack
    /// status. A failing DB save never turns a successful host operation into a reported failure.</summary>
    private async Task<(bool Ok, string Output)> ExecuteAndPersistAsync(
        Stack stack, bool deploy, HostPrep prep, string command,
        Func<Stream, CancellationToken, Task>? writeStdin, CancellationToken ct,
        Action<Stack>? beforePersist = null, string? successStatus = null, string? failureStatus = null)
    {
        bool ok;
        string output;
        try
        {
            (ok, output) = await ExecOnHostAsync(prep.Settings!, command, writeStdin, prep.Timeout, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Stack {Name} host op (deploy={Deploy}) failed.", stack.Name, deploy);
            stack.LastStatus = "Error";
            try { await _db.SaveChangesAsync(ct); } catch { /* best effort */ }
            if (deploy) { await _events.PublishAsync(ScheduleEvent.DeployFailed, stack.EnvironmentId, stack.Name, ct); }
            return (false, DockerErrorMessages.IsSshError(ex) ? DockerErrorMessages.DescribeSshError(ex) : $"Error: {ex.Message}");
        }

        beforePersist?.Invoke(stack);
        if (deploy && ok)
        {
            stack.LastDeployedAt = DateTime.UtcNow;
        }
        stack.LastStatus = ok
            ? (successStatus ?? (deploy ? "Deployed" : "Stopped"))
            : (failureStatus ?? (deploy ? "Deploy failed" : "Down failed"));
        try { await _db.SaveChangesAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Stack {Name}: status save after host op failed.", stack.Name); }

        if (deploy && !ok) { await _events.PublishAsync(ScheduleEvent.DeployFailed, stack.EnvironmentId, stack.Name, ct); }

        return (ok, string.IsNullOrWhiteSpace(output) ? (ok ? "OK." : "Failed.") : output.Trim());
    }

    /// <summary>
    /// Logs the target host into every enabled registry that has credentials, so private images can be
    /// pulled by the following <c>compose up/pull</c>. Best-effort: a failed login is logged, not fatal
    /// (the image may be public, or served by another registry).
    /// </summary>
    private async Task LoginRegistriesAsync(HostPrep prep, CancellationToken ct)
    {
        IReadOnlyList<RegistryLogin> logins;
        try
        {
            logins = await _registries.GetEnabledLoginsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load registry credentials for login.");
            return;
        }

        foreach (var reg in logins)
        {
            if (string.IsNullOrWhiteSpace(reg.Username) || string.IsNullOrWhiteSpace(reg.Token))
            {
                continue; // anonymous entry — nothing to log in with
            }

            try
            {
                var command = StackCommands.Login(prep.Head!, reg.Host, reg.Username);
                var token = Encoding.UTF8.GetBytes(reg.Token);
                var (ok, output) = await ExecOnHostAsync(prep.Settings!, command,
                    (s, c) => s.WriteAsync(token, c).AsTask(), TimeSpan.FromSeconds(30), ct, stream: false);
                if (!ok)
                {
                    _logger.LogWarning("docker login {Host} failed: {Output}", reg.Host, output.Trim());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "docker login {Host} errored.", reg.Host);
            }
        }
    }

    private async Task<(bool Ok, string Output)> ExecOnHostAsync(
        SshConnectionSettings settings, string command,
        Func<Stream, CancellationToken, Task>? writeStdin, TimeSpan timeout, CancellationToken ct, bool stream = true)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var client = _hostSessionFactory.Create(settings);
        await client.ConnectAsync(cts.Token);

        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout;

        // With an ambient sink (an API operation) the output is read live and forwarded as it arrives.
        var sink = stream ? CommandOutput.Current : null;
        if (sink is not null)
        {
            cmd.BufferOutput = false;
        }

        var async = cmd.BeginExecute();
        if (writeStdin is not null)
        {
            // SSH.NET 2026: input stream only after BeginExecute opened the channel. The writer streams
            // directly to the channel (e.g. a tar of the repo) so we never buffer the whole payload.
            var input = cmd.CreateInputStream();
            try
            {
                await writeStdin(input, cts.Token);
            }
            finally
            {
                // Always send EOF so the remote `cat`/`tar` unblocks, even if the write failed/timed out.
                input.Close();
            }
        }

        if (sink is null)
        {
            cmd.EndExecute(async);
            return (cmd.ExitStatus == 0, cmd.Result ?? string.Empty);
        }

        var output = await PumpOutputAsync(cmd, sink, cts.Token);
        cmd.EndExecute(async);
        return (cmd.ExitStatus == 0, output);
    }

    /// <summary>Reads a command's (merged) output until it ends, forwarding every chunk to <paramref name="sink"/>.
    /// A command that hangs past the timeout is disposed (process killed / channel closed) so the read ends.</summary>
    private static async Task<string> PumpOutputAsync(IHostCommand cmd, Action<string> sink, CancellationToken ct)
    {
        var all = new StringBuilder();
        using var abort = ct.Register(cmd.Dispose);
        try
        {
            using var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                var chunk = new string(buffer, 0, read);
                all.Append(chunk);
                try { sink(chunk); } catch { /* a failing sink never breaks the command */ }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException && ct.IsCancellationRequested)
        {
            // aborted by the timeout above; EndExecute reports the failure
        }

        return all.ToString();
    }

    /// <summary>Repo-relative compose file path for the stack (validated at save time). Defaults to
    /// <c>docker-compose.yml</c> for inline stacks and when the git path is empty.</summary>
    private static string EffectiveComposePath(Stack stack)
    {
        if (!stack.IsGitBacked)
        {
            return "docker-compose.yml";
        }

        var norm = VolumeFileCommands.NormalizeRelPath(stack.GitComposePath);
        return string.IsNullOrEmpty(norm) ? "docker-compose.yml" : norm;
    }

    private static void Apply(Stack stack, StackInput input)
    {
        stack.Name = input.Name.Trim();
        stack.EnvironmentId = input.EnvironmentId;
        if (input.ManagedBy is not null)
        {
            stack.ManagedBy = string.IsNullOrWhiteSpace(input.ManagedBy) ? null : input.ManagedBy.Trim();
        }

        var repo = string.IsNullOrWhiteSpace(input.GitRepoUrl) ? null : input.GitRepoUrl.Trim();
        stack.GitRepoUrl = repo;

        if (repo is null)
        {
            // Inline stack: keep the editor YAML + .env, clear any git source.
            stack.GitReference = null;
            stack.GitComposePath = null;
            stack.GitCredentialId = null;
            stack.ComposeYaml = input.ComposeYaml ?? string.Empty;
            stack.EnvContent = string.IsNullOrWhiteSpace(input.EnvContent) ? null : input.EnvContent;
        }
        else
        {
            // Git-backed: ComposeYaml is a cache filled on deploy; don't overwrite from the (hidden) editor.
            // The repo carries its own .env, so MatDock does not manage one.
            stack.GitReference = string.IsNullOrWhiteSpace(input.GitReference) ? null : input.GitReference.Trim();
            stack.GitComposePath = string.IsNullOrWhiteSpace(input.GitComposePath) ? null : input.GitComposePath.Trim();
            stack.GitCredentialId = input.GitCredentialId is > 0 ? input.GitCredentialId : null;
            stack.EnvContent = null;
        }
    }

    /// <summary>Validates the optional git source. Returns null when valid (or inline).</summary>
    private static string? ValidateGit(StackInput input)
    {
        if (string.IsNullOrWhiteSpace(input.GitRepoUrl))
        {
            return null; // inline stack
        }

        var url = input.GitRepoUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "Git URL must start with http:// or https://.";
        }

        // Validate the same (trimmed) value that Apply stores and EffectiveComposePath later normalizes.
        if (!string.IsNullOrWhiteSpace(input.GitComposePath)
            && VolumeFileCommands.NormalizeRelPath(input.GitComposePath.Trim()) is null)
        {
            return "Invalid compose path (no absolute paths or \"..\").";
        }

        return null;
    }
}
