using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Updates;

/// <summary>Reads and saves the update rules (global, per environment, per stack) and answers the effective rule of a stack.</summary>
public sealed class UpdatePolicyService
{
    private readonly MatDockDbContext _db;

    public UpdatePolicyService(MatDockDbContext db) => _db = db;

    /// <summary>The rule of one scope, or null when it sets nothing.</summary>
    public Task<UpdatePolicy?> GetAsync(UpdatePolicyScope scope, long scopeId, CancellationToken ct = default)
        => _db.UpdatePolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Scope == scope && p.ScopeId == scopeId, ct);

    /// <summary>Every rule, for resolving many stacks at once.</summary>
    public async Task<UpdatePolicySet> LoadAsync(CancellationToken ct = default)
        => new(await _db.UpdatePolicies.AsNoTracking().ToListAsync(ct));

    /// <summary>The effective rule of one stack.</summary>
    public async Task<EffectiveUpdatePolicy> ResolveAsync(Stack stack, CancellationToken ct = default)
        => (await LoadAsync(ct)).For(stack);

    /// <summary>Saves the rule of its scope (made or changed); a rule that sets nothing removes the scope's row.</summary>
    public async Task<(bool Ok, string Message)> SaveAsync(UpdatePolicy input, CancellationToken ct = default)
    {
        if (UpdatePolicies.Problem(input) is { } problem)
        {
            return (false, problem);
        }

        var existing = await _db.UpdatePolicies.FirstOrDefaultAsync(p => p.Scope == input.Scope && p.ScopeId == input.ScopeId, ct);
        if (UpdatePolicies.IsEmpty(input))
        {
            if (existing is not null)
            {
                _db.UpdatePolicies.Remove(existing); // soft delete
                await _db.SaveChangesAsync(ct);
            }

            return (true, "The rule is as the wider one says.");
        }

        if (existing is null)
        {
            existing = new UpdatePolicy { Scope = input.Scope, ScopeId = input.ScopeId };
            _db.UpdatePolicies.Add(existing);
        }

        existing.Mode = input.Mode;
        existing.Strategy = input.Strategy;
        existing.WindowStartHour = input.WindowStartHour;
        existing.WindowEndHour = input.WindowEndHour;
        existing.CheckEveryHours = input.CheckEveryHours;
        existing.PreBackup = input.PreBackup;
        existing.Rollback = input.Rollback;
        existing.NotifyAvailable = input.NotifyAvailable;
        existing.NotifyResult = input.NotifyResult;
        existing.ServiceStrategiesJson = UpdatePolicies.SerializeServiceStrategies(UpdatePolicies.ServiceStrategies(input.ServiceStrategiesJson));
        await _db.SaveChangesAsync(ct);
        return (true, "Rule saved.");
    }
}

/// <summary>All rules at hand: the effective rule of any stack without another query.</summary>
public sealed class UpdatePolicySet
{
    private readonly Dictionary<(UpdatePolicyScope, long), UpdatePolicy> _rows;

    public UpdatePolicySet(IEnumerable<UpdatePolicy> rows)
        => _rows = rows.GroupBy(r => (r.Scope, r.ScopeId)).ToDictionary(g => g.Key, g => g.First());

    public UpdatePolicy? Global => Row(UpdatePolicyScope.Global, 0);

    public UpdatePolicy? Row(UpdatePolicyScope scope, long scopeId) => _rows.GetValueOrDefault((scope, scopeId));

    public EffectiveUpdatePolicy For(Stack stack)
        => UpdatePolicies.Resolve(Global, Row(UpdatePolicyScope.Environment, stack.EnvironmentId), Row(UpdatePolicyScope.Stack, stack.Id));

    /// <summary>The rule a scope inherits (what is effective there when its own rule is empty): the environment's over the global one for a stack, the global one for an environment.</summary>
    public EffectiveUpdatePolicy Inherited(UpdatePolicyScope scope, long environmentId)
        => scope switch
        {
            UpdatePolicyScope.Stack => UpdatePolicies.Resolve(Global, Row(UpdatePolicyScope.Environment, environmentId), null),
            UpdatePolicyScope.Environment => UpdatePolicies.Resolve(Global, null, null),
            _ => EffectiveUpdatePolicy.Default,
        };
}
