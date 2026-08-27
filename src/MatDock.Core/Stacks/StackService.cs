using System.Text;
using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Ssh;
using MatDock.Core.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace MatDock.Core.Stacks;

/// <summary>CRUD for managed stacks plus deploy/down via <c>docker compose</c> over SSH.</summary>
public sealed class StackService
{
    private readonly MatDockDbContext _db;
    private readonly ISshClientFactory _sshClientFactory;
    private readonly EnvironmentService _environmentService;
    private readonly MatDockOptions _options;
    private readonly ILogger<StackService> _logger;

    public StackService(
        MatDockDbContext db,
        ISshClientFactory sshClientFactory,
        EnvironmentService environmentService,
        IOptions<MatDockOptions> options,
        ILogger<StackService> logger)
    {
        _db = db;
        _sshClientFactory = sshClientFactory;
        _environmentService = environmentService;
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

    public Task<(bool Ok, string Output)> DeployAsync(long id, CancellationToken ct = default)
        => RunComposeAsync(id, deploy: true, ct);

    public Task<(bool Ok, string Output)> DownAsync(long id, CancellationToken ct = default)
        => RunComposeAsync(id, deploy: false, ct);

    private async Task<(bool Ok, string Output)> RunComposeAsync(long id, bool deploy, CancellationToken ct)
    {
        var stack = await _db.Stacks.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stack is null)
        {
            return (false, "Stack nicht gefunden.");
        }

        var env = await _environmentService.GetAsync(stack.EnvironmentId, ct);
        if (env is null || !env.IsEnabled)
        {
            return (false, "Environment nicht verfügbar (deaktiviert oder gelöscht).");
        }

        var settings = _environmentService.BuildSettings(env);
        var head = VolumeCommands.DockerHead(settings.UseSudo, settings.DockerHost);
        // compose can pull images -> allow a generous timeout.
        var timeout = TimeSpan.FromSeconds(Math.Max(120, _options.MigrationTimeoutSeconds));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        bool ok;
        string output;
        try
        {
            using var client = _sshClientFactory.Create(settings);
            await client.ConnectAsync(cts.Token);

            using var cmd = client.CreateCommand(deploy
                ? StackCommands.Deploy(head, stack.Name)
                : StackCommands.Down(head, stack.Name));
            cmd.CommandTimeout = timeout;

            var async = cmd.BeginExecute();
            if (deploy)
            {
                // SSH.NET 2026: input stream only after BeginExecute opened the channel.
                var input = cmd.CreateInputStream();
                try
                {
                    var yaml = Encoding.UTF8.GetBytes(stack.ComposeYaml.Replace("\r\n", "\n"));
                    await input.WriteAsync(yaml, cts.Token);
                }
                finally
                {
                    // Always send EOF so the remote `cat` unblocks, even if the write failed/timed out.
                    input.Close();
                }
            }

            cmd.EndExecute(async);
            ok = cmd.ExitStatus == 0;
            output = cmd.Result ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Stack {Name} compose ({Deploy}) failed.", stack.Name, deploy);
            stack.LastStatus = "Fehler";
            try { await _db.SaveChangesAsync(ct); } catch { /* best effort */ }
            return (false, DockerErrorMessages.IsSshError(ex) ? DockerErrorMessages.DescribeSshError(ex) : $"Fehler: {ex.Message}");
        }

        // The host operation is done; persist status best-effort. A failing DB save must NOT turn a
        // successful compose into a reported failure.
        if (deploy && ok)
        {
            stack.LastDeployedAt = DateTime.UtcNow;
        }
        stack.LastStatus = ok ? (deploy ? "Deployed" : "Gestoppt") : (deploy ? "Deploy fehlgeschlagen" : "Down fehlgeschlagen");
        try { await _db.SaveChangesAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Stack {Name}: status save after compose failed.", stack.Name); }

        return (ok, string.IsNullOrWhiteSpace(output) ? (ok ? "OK." : "Fehlgeschlagen.") : output.Trim());
    }

    private static void Apply(Stack stack, StackInput input)
    {
        stack.Name = input.Name.Trim();
        stack.EnvironmentId = input.EnvironmentId;
        stack.ComposeYaml = input.ComposeYaml ?? string.Empty;
    }
}
