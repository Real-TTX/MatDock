namespace MatDock.Core.Entities;

/// <summary>What MatDock does by itself when a stack has a newer image.</summary>
public enum UpdateMode
{
    /// <summary>Nothing: updates are made by hand (or by a schedule).</summary>
    Off = 0,

    /// <summary>MatDock looks for updates and says so (once per new version); a person applies them.</summary>
    Notify = 1,

    /// <summary>MatDock looks, and applies the update itself (within the time window), with the safety options of the rule.</summary>
    Auto = 2,
}

/// <summary>Which newer image counts for a service.</summary>
public enum UpdateStrategy
{
    /// <summary>The same tag with a new digest (<c>latest</c>, <c>stable</c>, <c>16-alpine</c>): what the registry moved under the tag.</summary>
    Tag = 0,

    /// <summary>The newest version with the same major number and the same suffix series (<c>1.2.3</c> → <c>1.9.0</c>, <c>16.4-alpine</c> → <c>16.9-alpine</c>).</summary>
    Minor = 1,

    /// <summary>The newest version of the same suffix series, whatever the major number.</summary>
    Latest = 2,

    /// <summary>Never: the service stays as it is.</summary>
    Pinned = 3,
}

/// <summary>Where a rule applies; the narrowest one that sets a value decides it.</summary>
public enum UpdatePolicyScope
{
    /// <summary>Every stack that no narrower rule covers.</summary>
    Global = 0,

    /// <summary>The stacks of one environment (<see cref="UpdatePolicy.ScopeId"/> = the environment id).</summary>
    Environment = 1,

    /// <summary>One stack (<see cref="UpdatePolicy.ScopeId"/> = the stack id).</summary>
    Stack = 2,
}

/// <summary>
/// A rule for updates at one scope (global, an environment or a stack). Every value may be null = "as the wider rule says"; the effective rule of a stack is
/// made of its own, its environment's and the global one, narrowest first (<see cref="Updates.UpdatePolicies.Resolve"/>).
/// </summary>
public class UpdatePolicy : AuditableEntity
{
    public UpdatePolicyScope Scope { get; set; }

    /// <summary>The environment or stack the rule is for; 0 for the global rule.</summary>
    public long ScopeId { get; set; }

    public UpdateMode? Mode { get; set; }

    /// <summary>The strategy for the services of the stack that have none of their own (<see cref="ServiceStrategiesJson"/>).</summary>
    public UpdateStrategy? Strategy { get; set; }

    /// <summary>With <see cref="WindowEndHour"/>: the hours (server time, 0-23) in which MatDock may apply an update by itself. Start after end crosses midnight, equal means all day.
    /// Both null = as the wider rule says.</summary>
    public int? WindowStartHour { get; set; }

    public int? WindowEndHour { get; set; }

    /// <summary>How often MatDock looks for updates (hours, 1-168).</summary>
    public int? CheckEveryHours { get; set; }

    /// <summary>A bundle backup of the stack before MatDock applies an update; the update is not made when it fails.</summary>
    public bool? PreBackup { get; set; }

    /// <summary>Put the old images back when the containers are not up and healthy after an update.</summary>
    public bool? Rollback { get; set; }

    /// <summary>Say when a new version is there (once per version).</summary>
    public bool? NotifyAvailable { get; set; }

    /// <summary>Say how an update that MatDock applied ended.</summary>
    public bool? NotifyResult { get; set; }

    /// <summary>For a stack rule: the strategy per service, as JSON <c>{"web":"Minor","db":"Pinned"}</c>. A service that is not listed has <see cref="Strategy"/>.</summary>
    public string? ServiceStrategiesJson { get; set; }
}

/// <summary>
/// What MatDock has already said or tried about a new image of a service: one row per (stack, service, target), so a version is announced once and an update that
/// failed (and was rolled back) is not tried again until something newer is there.
/// </summary>
public class UpdateNotice : AuditableEntity
{
    public long StackId { get; set; }

    public string Service { get; set; } = string.Empty;

    /// <summary>The version tag (<c>1.9.0</c>) or the short registry digest the update would bring.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>MatDock tried to apply it and it did not work out: automatic updates leave it alone.</summary>
    public bool Failed { get; set; }

    /// <summary>When it was announced; null when it was only recorded (an update that was applied without an announcement).</summary>
    public DateTime? NotifiedUtc { get; set; }
}
