using MatDock.Core.Backups;
using MatDock.Core.Containers;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using MatDock.Core.Schedules;
using MatDock.Core.Stacks;

namespace MatDock.Web.Api;

/// <summary>Body of <c>POST /api/v1/stacks/{name}/backups</c>.</summary>
public sealed class CreateBackupBody
{
    /// <summary>Stop the containers during the backup (a consistent snapshot).</summary>
    public bool StopContainers { get; set; }
    public string? Note { get; set; }

    /// <summary>Backup target; null = the local target (/data/backups).</summary>
    public long? TargetId { get; set; }
}

/// <summary>Body of <c>POST /api/v1/backups/{id}/restore</c>.</summary>
public sealed class RestoreBackupBody
{
    /// <summary>Take a bundle of the current state first; the restore is aborted when it fails.</summary>
    public bool BackupCurrentFirst { get; set; }
}

/// <summary>Body of <c>PUT /api/v1/backup-schedules/{name}</c>; omitted fields take the defaults shown.</summary>
public sealed class BackupScheduleBody
{
    /// <summary>The stack to back up; null = every managed stack whose managedBy equals the schedule's.</summary>
    public string? Stack { get; set; }
    public string? ManagedBy { get; set; }

    /// <summary><c>daily</c> (default) or <c>weekly</c>.</summary>
    public string? Frequency { get; set; }

    /// <summary>"HH:mm" in UTC (default 03:00).</summary>
    public string? Time { get; set; }

    /// <summary>For weekly schedules (default Sunday).</summary>
    public string? Weekday { get; set; }

    /// <summary>Scheduled bundles kept per stack (default 7, 0 = unlimited).</summary>
    public int? Retention { get; set; }

    /// <summary>Default true.</summary>
    public bool? StopContainers { get; set; }

    /// <summary>Default true.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Backup target; null = local.</summary>
    public long? TargetId { get; set; }
}

/// <summary><c>/api/v1</c> backups (stack bundles) and bundle schedules, on top of <see cref="BundleBackupService"/>
/// and the unified <see cref="ScheduleService"/>.</summary>
public static class BackupsApi
{
    public const int MaxScheduleNameLength = 200;
    public const int MaxRetention = 1000;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/stacks/{name}/backups", CreateAsync);
        api.MapGet("/backups", ListAsync);
        api.MapPost("/backups/{id:long}/restore", RestoreAsync);
        api.MapDelete("/backups/{id:long}", DeleteAsync);
        api.MapGet("/backups/{id:long}/download", DownloadAsync);

        api.MapGet("/backup-schedules", ListSchedulesAsync);
        api.MapPut("/backup-schedules/{name}", PutScheduleAsync);
        api.MapDelete("/backup-schedules/{name}", DeleteScheduleAsync);
    }

    private static async Task<IResult> CreateAsync(HttpContext http, string name, EnvironmentService environments,
        StackService stacks, ContainerService containers, BackupTargetService targets, OperationRegistry operations)
    {
        if (!StackCommands.IsValidName(name))
        {
            return ApiResults.BadRequest("Invalid stack name.");
        }

        var (body, bodyError) = await ApiResults.ReadBodyAsync<CreateBackupBody>(http.Request);
        if (bodyError is not null)
        {
            return bodyError;
        }

        body ??= new CreateBackupBody();
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        if (body.TargetId is { } targetId && await targets.GetAsync(targetId, http.RequestAborted) is null)
        {
            return ApiResults.NotFound($"Backup target {targetId} not found.");
        }

        if (await stacks.GetByNameAsync(env.Id, name, http.RequestAborted) is null)
        {
            IReadOnlyList<DockerContainer> live;
            try
            {
                live = await containers.ListAsync(env, http.RequestAborted, includeStats: false);
            }
            catch (Exception ex)
            {
                return ApiResults.BadGateway($"Docker on '{env.Name}' did not answer: {ex.Message}");
            }

            if (!live.Any(c => string.Equals(c.Project, name, StringComparison.Ordinal)))
            {
                return ApiResults.NotFound($"Stack '{name}' not found.");
            }
        }

        var envId = env.Id;
        var operation = operations.Start("backup", name, StacksApi.LockKey(envId, name), async ctx =>
        {
            var ct = ctx.CancellationToken;
            var target = body.TargetId is { } id ? await ctx.Services.GetRequiredService<BackupTargetService>().GetAsync(id, ct) : null;
            var environment = await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(envId, ct);
            if (environment is null)
            {
                return OperationOutcome.Failure("Environment not found.");
            }

            ctx.Log($"==> Bundle backup of {name} to {target?.Name ?? "Local"}{(body.StopContainers ? " (containers stopped)" : string.Empty)}");
            var result = await ctx.Services.GetRequiredService<BundleBackupService>().CreateBundleAsync(
                environment, name, target, body.StopContainers, StackBackupKind.Manual, body.Note, ct: ct);
            ctx.Log(result.Message);
            return result.Ok
                ? OperationOutcome.Success(new { backupId = result.BackupId })
                : OperationOutcome.Failure(result.Message, new { backupId = result.BackupId });
        });

        return ApiResults.Accepted(new { operationId = operation.Id });
    }

    private static async Task<IResult> ListAsync(HttpContext http, string? stack, EnvironmentService environments, BundleBackupService bundles)
    {
        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        var list = await bundles.GetBundlesAsync(env.Id, string.IsNullOrWhiteSpace(stack) ? null : stack.Trim(), http.RequestAborted);
        return ApiResults.Ok(list.Select(ToDto));
    }

    /// <summary>The contract shape of a bundle (plus environment, target name and the run message).</summary>
    public static object ToDto(StackBackup b) => new
    {
        id = b.Id,
        stack = b.StackName,
        createdAt = ApiResults.Utc(b.CreateDate),
        sizeBytes = b.SizeBytes,
        volumeCount = b.VolumeCount,
        consistent = b.Consistent,
        kind = KindName(b.Kind),
        note = b.Note,
        status = b.Status.ToString().ToLowerInvariant(),
        targetId = b.BackupTargetId,
        targetName = b.BackupTargetName,
        environmentId = b.EnvironmentId,
        message = b.Message,
    };

    public static string KindName(StackBackupKind kind) => kind switch
    {
        StackBackupKind.Scheduled => "scheduled",
        StackBackupKind.PreUpdate => "preUpdate",
        _ => "manual",
    };

    private static async Task<IResult> RestoreAsync(HttpContext http, long id, EnvironmentService environments,
        BundleBackupService bundles, OperationRegistry operations)
    {
        var (body, bodyError) = await ApiResults.ReadBodyAsync<RestoreBackupBody>(http.Request);
        if (bodyError is not null)
        {
            return bodyError;
        }

        var record = await bundles.GetBundleAsync(id, http.RequestAborted);
        if (record is null)
        {
            return ApiResults.NotFound($"Backup {id} not found.");
        }

        if (record.Status != StackBackupStatus.Succeeded)
        {
            return ApiResults.Conflict($"Backup {id} is {record.Status.ToString().ToLowerInvariant()}; only a completed backup can be restored.");
        }

        var env = await environments.GetAsync(record.EnvironmentId, http.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            return ApiResults.Conflict("The environment of this backup is not available (disabled or deleted).");
        }

        var envId = env.Id;
        var backupCurrentFirst = body?.BackupCurrentFirst ?? false;
        var operation = operations.Start("restore", record.StackName, StacksApi.LockKey(envId, record.StackName), async ctx =>
        {
            var ct = ctx.CancellationToken;
            var environment = await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(envId, ct);
            if (environment is null)
            {
                return OperationOutcome.Failure("Environment not found.");
            }

            var (ok, message, safetyId) = await ctx.Services.GetRequiredService<BundleBackupService>()
                .RestoreAsync(id, environment, backupCurrentFirst, ctx.Log, ct);
            ctx.Log(message);
            var result = new { backupId = id, safetyBackupId = safetyId };
            return ok ? OperationOutcome.Success(result) : OperationOutcome.Failure(message, result);
        });

        return ApiResults.Accepted(new { operationId = operation.Id });
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, long id, BundleBackupService bundles)
    {
        var (found, error) = await bundles.DeleteBundleAsync(id, http.RequestAborted);
        if (!found)
        {
            return ApiResults.NotFound($"Backup {id} not found.");
        }

        return error is null ? Results.NoContent() : ApiResults.Conflict(error);
    }

    private static async Task<IResult> DownloadAsync(HttpContext http, long id, BundleBackupService bundles)
    {
        var record = await bundles.GetBundleAsync(id, http.RequestAborted);
        if (record is null)
        {
            return ApiResults.NotFound($"Backup {id} not found.");
        }

        if (record.Status != StackBackupStatus.Succeeded || string.IsNullOrEmpty(record.ManifestFileName))
        {
            return ApiResults.Conflict($"Backup {id} is not complete.");
        }

        var fileName = $"{record.StackName}_{record.CreateDate:yyyyMMddHHmmss}.mdbundle.tar";
        return Results.Stream(stream => bundles.WriteBundleTarAsync(record, stream, http.RequestAborted),
            "application/x-tar", fileName);
    }

    // ---- Bundle schedules -------------------------------------------------------------------

    private static async Task<IResult> ListSchedulesAsync(HttpContext http, ScheduleService schedules)
        => ApiResults.Ok((await schedules.GetBundleSchedulesAsync(http.RequestAborted)).Select(ToScheduleDto));

    /// <summary>The contract shape of a bundle schedule (plus id, environment, target, cron and the last run).</summary>
    public static object ToScheduleDto(ScheduledTask t)
    {
        var options = ScheduleOptions.Parse(t.OptionsJson);
        var (frequency, time, weekday) = ScheduleTimes.Describe(t.Cron);
        return new
        {
            id = t.Id,
            name = t.Name,
            stack = options.BundleStack,
            managedBy = t.ManagedBy,
            frequency,
            time,
            weekday,
            timeZone = "UTC",
            retention = options.RetentionCount,
            stopContainers = options.StopContainers,
            enabled = t.Enabled,
            targetId = options.BackupTargetId,
            environmentId = t.EnvironmentId,
            cron = t.Cron,
            nextRunAt = ApiResults.Utc(t.NextRunAt),
            lastRunAt = ApiResults.Utc(t.LastRunAt),
            lastStatus = t.LastStatus,
        };
    }

    /// <summary>Validates a schedule body and builds the ScheduleInput (without environment/target lookups); returns
    /// an error text instead when the body is invalid.</summary>
    public static (ScheduleInput? Input, string? Error) BuildScheduleInput(string name, BackupScheduleBody? body)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxScheduleNameLength || name.Any(char.IsControl))
        {
            return (null, $"Invalid schedule name (1-{MaxScheduleNameLength} characters, no control characters).");
        }

        body ??= new BackupScheduleBody();
        var stack = string.IsNullOrWhiteSpace(body.Stack) ? null : body.Stack.Trim();
        if (stack is not null && !StackCommands.IsValidName(stack))
        {
            return (null, "Invalid stack name.");
        }

        if (body.ManagedBy is { Length: > StacksApi.MaxManagedByLength })
        {
            return (null, $"managedBy is longer than {StacksApi.MaxManagedByLength} characters.");
        }

        var retention = body.Retention ?? 7;
        if (retention is < 0 or > MaxRetention)
        {
            return (null, $"retention must be between 0 and {MaxRetention}.");
        }

        if (!ScheduleTimes.TryBuildCron(body.Frequency ?? ScheduleTimes.Daily, body.Time ?? "03:00", body.Weekday ?? "Sunday",
                out var cron, out var cronError))
        {
            return (null, cronError);
        }

        return (new ScheduleInput
        {
            Name = name,
            Enabled = body.Enabled ?? true,
            Trigger = ScheduleTrigger.Cron,
            Cron = cron,
            Action = ScheduleAction.BundleBackup,
            BackupTargetId = body.TargetId,
            RetentionCount = retention,
            StopContainers = body.StopContainers ?? true,
            BundleStack = stack,
            ManagedBy = body.ManagedBy,
        }, null);
    }

    private static async Task<IResult> PutScheduleAsync(HttpContext http, string name, EnvironmentService environments,
        ScheduleService schedules, BackupTargetService targets)
    {
        var (body, bodyError) = await ApiResults.ReadBodyAsync<BackupScheduleBody>(http.Request);
        if (bodyError is not null)
        {
            return bodyError;
        }

        var (input, invalid) = BuildScheduleInput(name, body);
        if (input is null)
        {
            return ApiResults.BadRequest(invalid!);
        }

        if (input.BackupTargetId is { } targetId && await targets.GetAsync(targetId, http.RequestAborted) is null)
        {
            return ApiResults.NotFound($"Backup target {targetId} not found.");
        }

        var (env, error) = await MatDockApi.ResolveEnvironmentAsync(http, environments);
        if (env is null)
        {
            return error!;
        }

        input.EnvironmentId = env.Id;
        var existing = await schedules.FindBundleScheduleAsync(input.Name, http.RequestAborted);
        long id;
        if (existing is null)
        {
            var (ok, message, newId) = await schedules.CreateAsync(input, http.RequestAborted);
            if (!ok)
            {
                return ApiResults.BadRequest(message);
            }

            id = newId;
        }
        else
        {
            // Settings that only the Schedules page offers stay as the user set them.
            input.NotifyOnResult = existing.NotifyOnResult;
            input.RetentionDays = ScheduleOptions.Parse(existing.OptionsJson).RetentionDays;
            var (ok, message) = await schedules.UpdateAsync(existing.Id, input, http.RequestAborted);
            if (!ok)
            {
                return ApiResults.BadRequest(message);
            }

            id = existing.Id;
        }

        var saved = await schedules.GetAsync(id, http.RequestAborted);
        return ApiResults.Ok(ToScheduleDto(saved!));
    }

    private static async Task<IResult> DeleteScheduleAsync(HttpContext http, string name, ScheduleService schedules)
    {
        var existing = await schedules.FindBundleScheduleAsync((name ?? string.Empty).Trim(), http.RequestAborted);
        if (existing is null)
        {
            return ApiResults.NotFound($"Backup schedule '{name}' not found.");
        }

        await schedules.DeleteAsync(existing.Id, http.RequestAborted);
        return Results.NoContent();
    }
}
