using System.ComponentModel;
using MatDock.Core.Activity;
using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Core.Schedules;
using MatDock.Core.Stacks;
using MatDock.Core.Updates;
using MatDock.Web.Api;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatDock.Web.Mcp;

/// <summary>What runs around the hosts: operations, stack backups and restores, schedules, update rules, the activity log.</summary>
[McpServerToolType]
public static class AdminTools
{
    private const string WaitHelp = "Seconds to wait for the end (0 to 600, default 120); still running = an operation id for get_operation.";

    [McpServerTool(Name = "get_operation", ReadOnly = true), Description("How a long action stands (deploy, stack action, backup, restore): status (running, succeeded, failed), error, result and the end of its output. Operations are kept for an hour after they end.")]
    public static async Task<object> GetOperation(OperationRegistry operations,
        [Description("The operation id another tool answered.")] string operationId,
        [Description("Seconds to wait for the end first (0 to 600, default 0).")] int? waitSeconds = null, CancellationToken ct = default)
    {
        var operation = operations.Get(operationId.Trim()) ?? throw new McpException("Operation not found (unknown, or ended more than an hour ago).");
        return await McpScope.WaitAsync(operations, operation, waitSeconds ?? 0, ct);
    }

    [McpServerTool(Name = "list_backups", ReadOnly = true), Description("The bundle backups of stacks (volumes, compose file, .env and image references), newest first: id, stack, time, size, status, target, whether the containers were stopped (consistent).")]
    public static async Task<object> ListBackups(EnvironmentService environments, BundleBackupService bundles,
        [Description("The environment: id or name; left out = every environment.")] string? environment = null,
        [Description("Only the backups of this stack.")] string? stack = null,
        [Description("At most this many (default 50).")] int? take = null, CancellationToken ct = default)
    {
        long? envId = string.IsNullOrWhiteSpace(environment) ? null : (await McpScope.EnvironmentAsync(environments, environment, ct)).Id;
        var list = await bundles.GetBundlesAsync(envId, string.IsNullOrWhiteSpace(stack) ? null : stack.Trim(), ct);
        return list.Take(Math.Clamp(take ?? 50, 1, 500)).Select(BackupsApi.ToDto);
    }

    [McpServerTool(Name = "backup_stack", ReadOnly = false), Description("Takes a bundle backup of a stack (all its volumes, the compose file, the .env and the image references) to a backup target (left out = the local one). stopContainers stops them during the backup for a consistent copy.")]
    public static async Task<object> BackupStack(EnvironmentService environments, StackService stacks, ContainerService containers, BackupTargetService targets, OperationRegistry operations,
        [Description("The stack.")] string name,
        [Description("The environment: id or name (may be left out when there is only one).")] string? environment = null,
        [Description("Stop the containers during the backup.")] bool stopContainers = false,
        [Description("A note kept with the backup.")] string? note = null,
        [Description("The id of a backup target; left out = local.")] long? targetId = null,
        [Description(WaitHelp)] int? waitSeconds = null, CancellationToken ct = default)
    {
        var env = await McpScope.EnvironmentAsync(environments, environment, ct);
        if (!StackCommands.IsValidName(name))
        {
            throw new McpException($"'{name}' is no stack name.");
        }

        if (targetId is { } id && await targets.GetAsync(id, ct) is null)
        {
            throw new McpException($"Backup target {id} not found.");
        }

        if (await stacks.GetByNameAsync(env.Id, name, ct) is null && !(await containers.ListAsync(env, ct, includeStats: false)).Any(c => c.Project == name))
        {
            throw new McpException($"Stack '{name}' not found on {env.Name}.");
        }

        return await McpScope.WaitAsync(operations, StackOperations.Backup(operations, env.Id, name, targetId, stopContainers, note), waitSeconds, ct);
    }

    [McpServerTool(Name = "restore_backup", ReadOnly = false, Destructive = true), Description("Restores a bundle backup: its volumes are overwritten with the backup and the stack is deployed again (re-created when it is gone). With backupCurrentFirst (default true) a safety backup of the current state is taken first.")]
    public static async Task<object> RestoreBackup(EnvironmentService environments, BundleBackupService bundles, OperationRegistry operations,
        [Description("The id of the backup (list_backups).")] long backupId,
        [Description("Take a safety backup of the current state first (default true).")] bool backupCurrentFirst = true,
        [Description(WaitHelp)] int? waitSeconds = null, CancellationToken ct = default)
    {
        var record = await bundles.GetBundleAsync(backupId, ct) ?? throw new McpException($"Backup {backupId} not found.");
        if (record.Status != StackBackupStatus.Succeeded)
        {
            throw new McpException($"Backup {backupId} is {record.Status.ToString().ToLowerInvariant()}; only a completed backup can be restored.");
        }

        var env = await environments.GetAsync(record.EnvironmentId, ct);
        if (env is null || !env.IsEnabled)
        {
            throw new McpException("The environment of this backup is not available (disabled or deleted).");
        }

        return await McpScope.WaitAsync(operations, StackOperations.Restore(operations, env.Id, record, backupCurrentFirst), waitSeconds, ct);
    }

    [McpServerTool(Name = "list_schedules", ReadOnly = true), Description("The schedules (prune, backups, updates, scans, notifications on events, MatDock backup …): id, name, action, cron or event, enabled, last run with its result, next run.")]
    public static async Task<object> ListSchedules(ScheduleService schedules, CancellationToken ct = default)
        => (await schedules.GetAllAsync(ct)).Select(s => new
        {
            s.Id, s.Name, action = s.Action.ToString(), trigger = s.Trigger.ToString(), s.Cron, @event = s.Trigger == ScheduleTrigger.Event ? s.Event.ToString() : null,
            s.Enabled, s.EnvironmentId, lastRunAt = s.LastRunAt, lastRunOk = s.LastRunOk, lastStatus = s.LastStatus, nextRunAt = s.NextRunAt, managedBy = s.ManagedBy,
        });

    [McpServerTool(Name = "run_schedule", ReadOnly = false), Description("Runs a schedule now, whatever its time (what it does depends on its action: a prune, a backup, an update run …), and answers its result.")]
    public static async Task<string> RunSchedule(ScheduleService schedules,
        [Description("The id of the schedule (list_schedules).")] long scheduleId, CancellationToken ct = default)
        => await schedules.GetAsync(scheduleId, ct) is null
            ? throw new McpException($"Schedule {scheduleId} not found.")
            : await schedules.RunNowAsync(scheduleId, ct);

    [McpServerTool(Name = "list_update_rules", ReadOnly = true), Description("The rules of the update management: per scope (global, environment, stack) the mode (off, notify, auto), the strategy (tag, minor, latest, pinned, also per service), the time window, how often to look, backup first, rollback and notifications. A value null = as the wider rule says.")]
    public static async Task<object> ListUpdateRules(MatDockDbContext db, CancellationToken ct = default)
        => (await db.UpdatePolicies.AsNoTracking().ToListAsync(ct)).OrderBy(p => p.Scope).ThenBy(p => p.ScopeId).Select(p => new
        {
            scope = p.Scope.ToString().ToLowerInvariant(), id = p.ScopeId, mode = p.Mode?.ToString().ToLowerInvariant(), strategy = p.Strategy?.ToString().ToLowerInvariant(),
            p.WindowStartHour, p.WindowEndHour, p.CheckEveryHours, p.PreBackup, p.Rollback, p.NotifyAvailable, p.NotifyResult,
            serviceStrategies = UpdatePolicies.ServiceStrategies(p.ServiceStrategiesJson).ToDictionary(s => s.Key, s => s.Value.ToString().ToLowerInvariant()),
        });

    [McpServerTool(Name = "set_update_rule", ReadOnly = false), Description("Sets the update rule of a scope: global (id 0), environment (its id) or stack (its id). Every value left out is 'as the wider rule says'; a rule without values falls back to the wider one entirely.")]
    public static async Task<string> SetUpdateRule(UpdatePolicyService rules,
        [Description("global, environment or stack.")] string scope,
        [Description("0 for global, else the id of the environment or stack.")] long id,
        [Description("off, notify or auto.")] string? mode = null,
        [Description("tag, minor, latest or pinned.")] string? strategy = null,
        [Description("Auto updates only from this hour (0-23, server time).")] int? windowStartHour = null,
        [Description("… until this hour.")] int? windowEndHour = null,
        [Description("Look for updates every so many hours (1-168).")] int? checkEveryHours = null,
        [Description("A bundle backup before an automatic update.")] bool? preBackup = null,
        [Description("Put the old images back when the containers are not healthy after an update.")] bool? rollback = null,
        [Description("Say when a new version is there.")] bool? notifyAvailable = null,
        [Description("Say how an automatic update ended.")] bool? notifyResult = null,
        [Description("Hours a new image waits (since MatDock first saw it) before an automatic update applies it (0-720).")] int? minAgeHours = null,
        [Description("Hold back new images with vulnerabilities: off, any, criticalhigh, critical or morethancurrent.")] string? vulnerabilityGate = null,
        CancellationToken ct = default)
    {
        UpdatePolicyScope s = scope.Trim().ToLowerInvariant() switch
        {
            "global" when id == 0 => UpdatePolicyScope.Global,
            "environment" when id > 0 => UpdatePolicyScope.Environment,
            "stack" when id > 0 => UpdatePolicyScope.Stack,
            _ => throw new McpException("The scope is global (id 0), environment or stack (with its id)."),
        };
        var policy = new UpdatePolicy
        {
            Scope = s, ScopeId = id, Mode = Parse<UpdateMode>(mode, "mode"), Strategy = Parse<UpdateStrategy>(strategy, "strategy"),
            WindowStartHour = windowStartHour, WindowEndHour = windowEndHour, CheckEveryHours = checkEveryHours,
            PreBackup = preBackup, Rollback = rollback, NotifyAvailable = notifyAvailable, NotifyResult = notifyResult,
            MinAgeHours = minAgeHours, VulnerabilityGate = Parse<VulnerabilityGate>(vulnerabilityGate, "vulnerabilityGate"),
        };
        var (ok, message) = await rules.SaveAsync(policy, ct);
        return ok ? message : throw new McpException(message);
    }

    [McpServerTool(Name = "list_activity", ReadOnly = true), Description("The activity log, newest first: who did what to which object on which environment and how it ended (actions of users, API and MCP calls).")]
    public static async Task<object> ListActivity(ActivityLogService activity,
        [Description("Only entries containing this text (user, action, target, environment).")] string? search = null,
        [Description("Only the failed ones.")] bool failedOnly = false,
        [Description("At most this many (default 50).")] int? take = null, CancellationToken ct = default)
    {
        var (items, total) = await activity.QueryAsync(search, failedOnly, 1, Math.Clamp(take ?? 50, 1, 500), ct);
        return new
        {
            total,
            entries = items.Select(e => new { time = e.CreateDate, e.Actor, e.Source, e.Action, e.Target, e.Environment, e.Ok, e.Message }),
        };
    }

    private static T? Parse<T>(string? value, string field) where T : struct, Enum
        => string.IsNullOrWhiteSpace(value) ? null
            : Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed
            : throw new McpException($"Unknown {field} '{value}': {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");
}
