using System.Text.Json;

namespace MatDock.Core.Stacks;

/// <summary>
/// How a stack is deployed: pull newer images first, build the images of services with a <c>build:</c> section (with the newest base images,
/// without the build cache), re-create every container, remove the containers of services that left the file. A stack keeps its own default
/// (<see cref="Entities.Stack.DeployOptionsJson"/>), used by the list, the webhook, a sync and a schedule; the editor can change it per deploy.
/// </summary>
public sealed record DeployOptions
{
    public static readonly DeployOptions None = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary><c>compose pull</c> before <c>up</c> (pinned services are left alone).</summary>
    public bool Pull { get; init; }

    /// <summary><c>compose build</c> before <c>up</c>: the images of the services with a <c>build:</c> section.</summary>
    public bool Build { get; init; }

    /// <summary><c>compose build --pull</c>: the newest versions of the base images (<c>FROM</c>). Implies a build.</summary>
    public bool PullBase { get; init; }

    /// <summary><c>compose build --no-cache</c>. Implies a build.</summary>
    public bool NoCache { get; init; }

    /// <summary><c>up --force-recreate</c>: new containers even when nothing changed.</summary>
    public bool ForceRecreate { get; init; }

    /// <summary><c>up --remove-orphans</c>: removes the containers of services that are no longer in the file.</summary>
    public bool RemoveOrphans { get; init; }

    /// <summary>Whether a build step runs.</summary>
    public bool Builds => Build || PullBase || NoCache;

    public bool IsNone => this == None;

    /// <summary>The options in words, for the history and the log ("pull, build, force recreate"); empty for none.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Pull) { parts.Add("pull"); }
        if (Builds) { parts.Add(PullBase && NoCache ? "build (base images, no cache)" : PullBase ? "build (base images)" : NoCache ? "build (no cache)" : "build"); }
        if (ForceRecreate) { parts.Add("force recreate"); }
        if (RemoveOrphans) { parts.Add("remove orphans"); }
        return string.Join(", ", parts);
    }

    /// <summary>Adds the options of <paramref name="other"/> (an update adds a pull to the stack's own options).</summary>
    public DeployOptions Union(DeployOptions other) => new()
    {
        Pull = Pull || other.Pull,
        Build = Build || other.Build,
        PullBase = PullBase || other.PullBase,
        NoCache = NoCache || other.NoCache,
        ForceRecreate = ForceRecreate || other.ForceRecreate,
        RemoveOrphans = RemoveOrphans || other.RemoveOrphans,
    };

    public static DeployOptions FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        try
        {
            return JsonSerializer.Deserialize<DeployOptions>(json, Json) ?? None;
        }
        catch (JsonException)
        {
            return None;
        }
    }

    /// <summary>JSON for the stack, null for none (nothing stored).</summary>
    public string? ToJson() => IsNone ? null : JsonSerializer.Serialize(this, Json);
}

/// <summary>What started a deploy, as kept in the history (<see cref="Entities.StackDeployment.Trigger"/>).</summary>
public static class DeployTrigger
{
    public const string Manual = "manual";
    public const string Webhook = "webhook";
    public const string Schedule = "schedule";
    public const string Sync = "sync";
    public const string Update = "update";
    public const string Rollback = "rollback";
    public const string Restore = "restore";
    public const string Copy = "copy";
    public const string Api = "api";
    public const string Mcp = "mcp";

    /// <summary>The label of a trigger on the page.</summary>
    public static string Label(string? trigger) => trigger switch
    {
        Manual => "Manual",
        Webhook => "Webhook",
        Schedule => "Schedule",
        Sync => "Git sync",
        Update => "Update",
        Rollback => "Rollback",
        Restore => "Restore",
        Copy => "Copy",
        Api => "API",
        Mcp => "MCP",
        null or "" => "–",
        _ => trigger,
    };
}

/// <summary>A deploy as it is asked for: the options, what started it and who.</summary>
/// <param name="Options">Null = the stack's own default options.</param>
public sealed record DeployRequest(DeployOptions? Options = null, string Trigger = DeployTrigger.Manual, string? Actor = null)
{
    public static readonly DeployRequest Default = new();
}

/// <summary>How a sync of a Git stack ended.</summary>
public enum GitSyncOutcome
{
    /// <summary>The compose folder changed: the stack was deployed.</summary>
    Deployed,

    /// <summary>No new commit, or one that changed nothing in the compose folder: nothing was deployed.</summary>
    Skipped,

    Failed,
}

/// <summary>What a sync of a Git stack did (<see cref="StackService.SyncGitAsync"/>), with the commit it saw.</summary>
public sealed record GitSyncResult(GitSyncOutcome Outcome, string Message, string? Commit = null);
