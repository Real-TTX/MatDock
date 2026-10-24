using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MatDock.Core.Security;

/// <summary>
/// Keeps the <c>.env</c> of a stack (<see cref="Stack.EnvContent"/>: database passwords, API keys) encrypted in the database. Writing encrypts it, reading
/// decrypts it, so the rest of MatDock sees the plain text as before. A value written by an older MatDock (plain) is read as it is and encrypted the next
/// time the stack is saved (<see cref="EncryptExistingAsync"/> does it for all at the start). The prefix tells an encrypted value apart.
/// </summary>
public sealed class StackSecretsInterceptor : SaveChangesInterceptor, IMaterializationInterceptor
{
    /// <summary>What an encrypted value starts with.</summary>
    public const string Prefix = "enc1:";

    private readonly ISecretProtector? _secrets;

    // Plain values of the stacks of one SaveChanges, put back into the entities afterwards.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, List<(EntityEntry Entry, string Plain)>> _pending = new();

    /// <summary>An instance with its own protector (for code that encrypts a value outside the database, like a bundle backup, and for tests).</summary>
    public StackSecretsInterceptor(ISecretProtector secrets) => _secrets = secrets;

    private StackSecretsInterceptor()
    {
    }

    /// <summary>
    /// The one instance the database contexts of the app use: it takes the protector of the context's own application. One instance for every host
    /// (also every test host) means one set of EF services, instead of one per interceptor instance.
    /// </summary>
    public static StackSecretsInterceptor Shared { get; } = new();

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The encrypted form of a value (null and empty stay as they are).</summary>
    public string? Protect(string? plain) => Protect(plain, _secrets);

    /// <summary>The plain form of a value; a plain value is returned as it is. Throws when it was encrypted with other keys.</summary>
    public string? Unprotect(string? value) => Unprotect(value, _secrets);

    private static string? Protect(string? plain, ISecretProtector? secrets)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain) || secrets is null)
        {
            return plain;
        }

        return Prefix + secrets.Protect(plain);
    }

    private static string? Unprotect(string? value, ISecretProtector? secrets)
        => IsProtected(value) && secrets is not null ? secrets.Unprotect(value![Prefix.Length..]) : value;

    private ISecretProtector? SecretsOf(DbContext? context)
    {
        if (_secrets is not null || context is null)
        {
            return _secrets;
        }

        var app = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
        return app?.GetService(typeof(ISecretProtector)) as ISecretProtector;
    }

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (entity is Stack stack && IsProtected(stack.EnvContent))
        {
            stack.EnvContent = Unprotect(stack.EnvContent, SecretsOf(materializationData.Context));
        }

        return entity;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Encrypt(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Encrypt(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Restore(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Restore(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Restore(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Restore(eventData.Context);
        return Task.CompletedTask;
    }

    private void Encrypt(DbContext? context)
    {
        var secrets = SecretsOf(context);
        if (context is null || secrets is null)
        {
            return; // no protector (a context without the app): nothing is encrypted, nothing is lost
        }

        var pending = new List<(EntityEntry, string)>();
        foreach (var entry in context.ChangeTracker.Entries<Stack>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var property = entry.Property(s => s.EnvContent);
            var plain = property.CurrentValue;
            if (string.IsNullOrEmpty(plain) || IsProtected(plain))
            {
                continue;
            }

            property.CurrentValue = Protect(plain, secrets);
            pending.Add((entry, plain));
        }

        if (pending.Count > 0)
        {
            _pending.AddOrUpdate(context, pending);
        }
    }

    private void Restore(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending))
        {
            return;
        }

        _pending.Remove(context);
        foreach (var (entry, plain) in pending)
        {
            if (entry.State == EntityState.Detached)
            {
                ((Stack)entry.Entity).EnvContent = plain;
                continue;
            }

            var property = entry.Property(nameof(Stack.EnvContent));
            property.CurrentValue = plain;
            property.OriginalValue = plain;
            property.IsModified = false;
        }
    }

    /// <summary>Encrypts the <c>.env</c> of every stack that still has it in plain text (written by an older MatDock). Answers how many it changed.</summary>
    public static async Task<int> EncryptExistingAsync(Data.MatDockDbContext db, CancellationToken ct = default)
    {
        var plain = await db.Stacks.IgnoreQueryFilters()
            .Where(s => s.EnvContent != null && s.EnvContent != "" && !s.EnvContent.StartsWith(Prefix))
            .ToListAsync(ct);
        foreach (var stack in plain)
        {
            db.Entry(stack).Property(s => s.EnvContent).IsModified = true;
        }

        if (plain.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return plain.Count;
    }
}
