using System.Text.Json;
using MatDock.Core.Entities;

namespace MatDock.Core.Updates;

/// <summary>The rule that applies to one stack, all values set: its own rule over its environment's over the global one over <see cref="Default"/>.</summary>
public sealed record EffectiveUpdatePolicy(
    UpdateMode Mode,
    UpdateStrategy Strategy,
    IReadOnlyDictionary<string, UpdateStrategy> ServiceStrategies,
    int? WindowStartHour,
    int? WindowEndHour,
    int CheckEveryHours,
    bool PreBackup,
    bool Rollback,
    bool NotifyAvailable,
    bool NotifyResult)
{
    /// <summary>What a stack gets when no rule says anything: nothing happens by itself, and what MatDock does is careful.</summary>
    public static readonly EffectiveUpdatePolicy Default = new(
        UpdateMode.Off, UpdateStrategy.Tag, new Dictionary<string, UpdateStrategy>(), null, null, CheckEveryHours: 6,
        PreBackup: true, Rollback: true, NotifyAvailable: true, NotifyResult: true);

    /// <summary>The strategy of a service: its own entry in the stack's rule, else the strategy of the rule.</summary>
    public UpdateStrategy StrategyOf(string service) => ServiceStrategies.TryGetValue(service, out var s) ? s : Strategy;

    /// <summary>Whether MatDock may apply an update by itself at this (server) time.</summary>
    public bool InWindow(DateTime localNow)
    {
        if (WindowStartHour is not { } start || WindowEndHour is not { } end || start == end)
        {
            return true;
        }

        var hour = localNow.Hour;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }
}

public static class UpdatePolicies
{
    public const int MinCheckHours = 1;
    public const int MaxCheckHours = 168;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The effective rule of a stack: for each value the narrowest rule that sets it decides.</summary>
    public static EffectiveUpdatePolicy Resolve(UpdatePolicy? global, UpdatePolicy? environment, UpdatePolicy? stack)
    {
        var chain = new[] { stack, environment, global };
        var d = EffectiveUpdatePolicy.Default;

        T Pick<T>(Func<UpdatePolicy, T?> get, T fallback) where T : struct
            => chain.Select(p => p is null ? null : get(p)).FirstOrDefault(v => v is not null) ?? fallback;

        // The window is one thing: both hours from the narrowest rule that sets them.
        var window = chain.FirstOrDefault(p => p is { WindowStartHour: not null, WindowEndHour: not null });

        return new EffectiveUpdatePolicy(
            Pick(p => p.Mode, d.Mode),
            Pick(p => p.Strategy, d.Strategy),
            ServiceStrategies(stack?.ServiceStrategiesJson),
            window?.WindowStartHour,
            window?.WindowEndHour,
            Math.Clamp(Pick(p => p.CheckEveryHours, d.CheckEveryHours), MinCheckHours, MaxCheckHours),
            Pick(p => p.PreBackup, d.PreBackup),
            Pick(p => p.Rollback, d.Rollback),
            Pick(p => p.NotifyAvailable, d.NotifyAvailable),
            Pick(p => p.NotifyResult, d.NotifyResult));
    }

    /// <summary>The per-service strategies of a stack rule (entries with a bad name or value are left out).</summary>
    public static IReadOnlyDictionary<string, UpdateStrategy> ServiceStrategies(string? json)
    {
        var result = new SortedDictionary<string, UpdateStrategy>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            foreach (var (service, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [])
            {
                if (Stacks.StackCommands.IsValidServiceName(service) && Enum.TryParse<UpdateStrategy>(value, ignoreCase: true, out var strategy) && Enum.IsDefined(strategy))
                {
                    result[service] = strategy;
                }
            }
        }
        catch (JsonException)
        {
            // an unreadable value counts as none
        }

        return result;
    }

    public static string? SerializeServiceStrategies(IReadOnlyDictionary<string, UpdateStrategy> strategies)
        => strategies.Count == 0
            ? null
            : JsonSerializer.Serialize(strategies.OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key, s => s.Value.ToString()), Json);

    /// <summary>The problem with a rule's values, or null when they are fine.</summary>
    public static string? Problem(UpdatePolicy policy)
    {
        if ((policy.WindowStartHour is null) != (policy.WindowEndHour is null))
        {
            return "The time window needs a start and an end hour.";
        }

        if (policy.WindowStartHour is { } s && s is < 0 or > 23 || policy.WindowEndHour is { } e && e is < 0 or > 23)
        {
            return "The hours of the time window are 0 to 23.";
        }

        if (policy.CheckEveryHours is { } h && h is < MinCheckHours or > MaxCheckHours)
        {
            return $"Looking for updates every {MinCheckHours} to {MaxCheckHours} hours.";
        }

        if (policy.Mode is { } m && !Enum.IsDefined(m) || policy.Strategy is { } st && !Enum.IsDefined(st))
        {
            return "Unknown mode or strategy.";
        }

        if (policy.Scope == UpdatePolicyScope.Global ? policy.ScopeId != 0 : policy.ScopeId <= 0)
        {
            return "The rule needs the environment or stack it is for.";
        }

        if (policy.Scope != UpdatePolicyScope.Stack && !string.IsNullOrWhiteSpace(policy.ServiceStrategiesJson))
        {
            return "Strategies per service belong to the rule of a stack.";
        }

        return null;
    }

    /// <summary>A rule that sets nothing (it can be removed).</summary>
    public static bool IsEmpty(UpdatePolicy p)
        => p.Mode is null && p.Strategy is null && p.WindowStartHour is null && p.WindowEndHour is null && p.CheckEveryHours is null
           && p.PreBackup is null && p.Rollback is null && p.NotifyAvailable is null && p.NotifyResult is null
           && ServiceStrategies(p.ServiceStrategiesJson).Count == 0;
}
