using System.Formats.Tar;
using System.Text;
using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Git;
using MatDock.Core.Ssh;
using MatDock.Core.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace MatDock.Core.Stacks;

/// <summary>CRUD for managed stacks plus deploy/down via <c>docker compose</c> over SSH. A stack is either
/// inline (compose YAML from the editor) or git-backed (repo cloned and the whole tree shipped to the host).</summary>
public sealed class StackService
{
    private readonly MatDockDbContext _db;
    private readonly ISshClientFactory _sshClientFactory;
    private readonly EnvironmentService _environmentService;
    private readonly GitCredentialService _gitCredentials;
    private readonly GitRepositoryService _gitRepo;
    private readonly MatDockOptions _options;
    private readonly ILogger<StackService> _logger;

    public StackService(
        MatDockDbContext db,
        ISshClientFactory sshClientFactory,
        EnvironmentService environmentService,
        GitCredentialService gitCredentials,
        GitRepositoryService gitRepo,
        IOptions<MatDockOptions> options,
        ILogger<StackService> logger)
    {
        _db = db;
        _sshClientFactory = sshClientFactory;
        _environmentService = environmentService;
        _gitCredentials = gitCredentials;
        _gitRepo = gitRepo;
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
            return (false, "Ungültiger Stack-Name (klein, a-z 0-9 _ -, Beginn alphanumerisch).", 0);
        }

        var gitError = ValidateGit(input);
        if (gitError is not null)
        {
            return (false, gitError, 0);
        }

        var name = input.Name.Trim();
        if (await _db.Stacks.AnyAsync(s => s.Name == name && s.EnvironmentId == input.EnvironmentId, ct))
        {
            return (false, "In diesem Environment existiert bereits ein Stack mit diesem Namen.", 0);
        }

        var stack = new Stack();
        Apply(stack, input);
        _db.Stacks.Add(stack);
        await _db.SaveChangesAsync(ct);
        return (true, "Stack angelegt.", stack.Id);
    }

    public async Task<(bool Ok, string Message)> UpdateAsync(long id, StackInput input, CancellationToken ct = default)
    {
        if (!StackCommands.IsValidName(input.Name))
        {
            return (false, "Ungültiger Stack-Name (klein, a-z 0-9 _ -, Beginn alphanumerisch).");
        }

        var gitError = ValidateGit(input);
        if (gitError is not null)
        {
            return (false, gitError);
        }

        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return (false, "Stack nicht gefunden.");
        }

        var name = input.Name.Trim();
        if (await _db.Stacks.AnyAsync(s => s.Id != id && s.Name == name && s.EnvironmentId == input.EnvironmentId, ct))
        {
            return (false, "In diesem Environment existiert bereits ein Stack mit diesem Namen.");
        }

        Apply(stack, input);
        await _db.SaveChangesAsync(ct);
        return (true, "Stack gespeichert.");
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
            return (false, "Stack nicht gefunden.");
        }

        return stack.IsGitBacked ? await GitDeployAsync(stack, ct) : await InlineDeployAsync(stack, ct);
    }

    public async Task<(bool Ok, string Output)> DownAsync(long id, CancellationToken ct = default)
    {
        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return (false, "Stack nicht gefunden.");
        }

        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        var command = StackCommands.Down(prep.Head!, stack.Name, EffectiveComposePath(stack));
        return await ExecuteAndPersistAsync(stack, deploy: false, prep, command, stdin: null, ct);
    }

    private async Task<(bool Ok, string Output)> InlineDeployAsync(Stack stack, CancellationToken ct)
    {
        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        var yaml = Encoding.UTF8.GetBytes(stack.ComposeYaml.Replace("\r\n", "\n"));
        var command = StackCommands.Deploy(prep.Head!, stack.Name);
        return await ExecuteAndPersistAsync(stack, deploy: true, prep, command, yaml, ct);
    }

    private async Task<(bool Ok, string Output)> GitDeployAsync(Stack stack, CancellationToken ct)
    {
        var prep = await PrepareHostAsync(stack, ct);
        if (!prep.Ok)
        {
            return (false, prep.Message);
        }

        var composePath = EffectiveComposePath(stack);

        GitCredentialSecret? secret = null;
        if (stack.GitCredentialId is > 0)
        {
            secret = await _gitCredentials.ResolveAsync(stack.GitCredentialId.Value, ct);
            if (secret is null)
            {
                return (false, "Zugewiesener Git-Zugang wurde nicht gefunden.");
            }
        }

        string? workDir = null;
        try
        {
            workDir = await _gitRepo.CloneToTempAsync(stack.GitRepoUrl!, stack.GitReference, secret, ct);

            var composeFull = Path.Combine(workDir, composePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(composeFull))
            {
                stack.LastStatus = "Compose-Datei fehlt";
                try { await _db.SaveChangesAsync(ct); } catch { /* best effort */ }
                return (false, $"Compose-Datei im Repo nicht gefunden: {composePath}");
            }

            var composeContent = await File.ReadAllTextAsync(composeFull, ct);

            byte[] tar;
            using (var ms = new MemoryStream())
            {
                TarFile.CreateFromDirectory(workDir, ms, includeBaseDirectory: false);
                tar = ms.ToArray();
            }

            var command = StackCommands.GitSync(prep.Head!, stack.Name, composePath);
            return await ExecuteAndPersistAsync(stack, deploy: true, prep, command, tar, ct,
                beforePersist: s => s.ComposeYaml = composeContent);
        }
        catch (GitOperationException ex)
        {
            stack.LastStatus = "Git-Fehler";
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

    private sealed record HostPrep(bool Ok, string Message, SshConnectionSettings? Settings, string? Head, TimeSpan Timeout);

    private async Task<HostPrep> PrepareHostAsync(Stack stack, CancellationToken ct)
    {
        var env = await _environmentService.GetAsync(stack.EnvironmentId, ct);
        if (env is null || !env.IsEnabled)
        {
            return new HostPrep(false, "Environment nicht verfügbar (deaktiviert oder gelöscht).", null, null, default);
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
        Stack stack, bool deploy, HostPrep prep, string command, byte[]? stdin, CancellationToken ct,
        Action<Stack>? beforePersist = null)
    {
        bool ok;
        string output;
        try
        {
            (ok, output) = await ExecOnHostAsync(prep.Settings!, command, stdin, prep.Timeout, ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Stack {Name} host op (deploy={Deploy}) failed.", stack.Name, deploy);
            stack.LastStatus = "Fehler";
            try { await _db.SaveChangesAsync(ct); } catch { /* best effort */ }
            return (false, DockerErrorMessages.IsSshError(ex) ? DockerErrorMessages.DescribeSshError(ex) : $"Fehler: {ex.Message}");
        }

        beforePersist?.Invoke(stack);
        if (deploy && ok)
        {
            stack.LastDeployedAt = DateTime.UtcNow;
        }
        stack.LastStatus = ok ? (deploy ? "Deployed" : "Gestoppt") : (deploy ? "Deploy fehlgeschlagen" : "Down fehlgeschlagen");
        try { await _db.SaveChangesAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Stack {Name}: status save after host op failed.", stack.Name); }

        return (ok, string.IsNullOrWhiteSpace(output) ? (ok ? "OK." : "Fehlgeschlagen.") : output.Trim());
    }

    private async Task<(bool Ok, string Output)> ExecOnHostAsync(
        SshConnectionSettings settings, string command, byte[]? stdin, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var client = _sshClientFactory.Create(settings);
        await client.ConnectAsync(cts.Token);

        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout;

        var async = cmd.BeginExecute();
        if (stdin is not null)
        {
            // SSH.NET 2026: input stream only after BeginExecute opened the channel.
            var input = cmd.CreateInputStream();
            try
            {
                await input.WriteAsync(stdin, cts.Token);
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
            return "Git-URL muss mit http:// oder https:// beginnen.";
        }

        if (!string.IsNullOrWhiteSpace(input.GitComposePath)
            && VolumeFileCommands.NormalizeRelPath(input.GitComposePath) is null)
        {
            return "Ungültiger Compose-Pfad (keine absoluten Pfade oder \"..\").";
        }

        return null;
    }
}
