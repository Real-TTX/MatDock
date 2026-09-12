using System.Formats.Tar;
using System.Text;
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

    public async Task<(bool Ok, string Output)> DownAsync(long id, CancellationToken ct = default)
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

        var command = StackCommands.Down(prep.Head!, stack.Name, EffectiveComposePath(stack));
        return await ExecuteAndPersistAsync(stack, deploy: false, prep, command, writeStdin: null, ct);
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

    private async Task<(bool Ok, string Output)> InlineDeployAsync(Stack stack, bool pull, CancellationToken ct)
    {
        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        await LoginRegistriesAsync(prep, ct);

        var yaml = Encoding.UTF8.GetBytes(stack.ComposeYaml.Replace("\r\n", "\n"));
        var command = pull ? StackCommands.Update(prep.Head!, stack.Name) : StackCommands.Deploy(prep.Head!, stack.Name);
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
        var composeFull = Path.Combine(workDir, composePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(composeFull))
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
                    var path = Path.Combine(dir, sidecar);
                    if (File.Exists(path))
                    {
                        meta = AppMetadataParser.Merge(meta, AppMetadataParser.FromSidecar(File.ReadAllText(path), sidecar));
                        break;
                    }
                }

                meta = ResolveIconFile(meta, dir);
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

    private static AppMetadata? ResolveIconFile(AppMetadata? meta, string dir)
    {
        if (meta is null)
        {
            return null;
        }

        try
        {
            if (AppIcons.IsFileName(meta.Icon))
            {
                var path = Path.Combine(dir, meta.Icon!.Replace('/', Path.DirectorySeparatorChar));
                meta.Icon = File.Exists(path) ? AppIcons.ToDataUrl(File.ReadAllBytes(path), path) : null;
            }

            if (string.IsNullOrWhiteSpace(meta.Icon))
            {
                foreach (var name in AppIcons.DefaultFileNames)
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path))
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
                    (s, c) => s.WriteAsync(token, c).AsTask(), TimeSpan.FromSeconds(30), ct);
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
        Func<Stream, CancellationToken, Task>? writeStdin, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var client = _hostSessionFactory.Create(settings);
        await client.ConnectAsync(cts.Token);

        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout;

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

        cmd.EndExecute(async);
        return (cmd.ExitStatus == 0, cmd.Result ?? string.Empty);
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

        var repo = string.IsNullOrWhiteSpace(input.GitRepoUrl) ? null : input.GitRepoUrl.Trim();
        stack.GitRepoUrl = repo;

        if (repo is null)
        {
            // Inline stack: keep the editor YAML, clear any git source.
            stack.GitReference = null;
            stack.GitComposePath = null;
            stack.GitCredentialId = null;
            stack.ComposeYaml = input.ComposeYaml ?? string.Empty;
        }
        else
        {
            // Git-backed: ComposeYaml is a cache filled on deploy; don't overwrite from the (hidden) editor.
            stack.GitReference = string.IsNullOrWhiteSpace(input.GitReference) ? null : input.GitReference.Trim();
            stack.GitComposePath = string.IsNullOrWhiteSpace(input.GitComposePath) ? null : input.GitComposePath.Trim();
            stack.GitCredentialId = input.GitCredentialId is > 0 ? input.GitCredentialId : null;
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
