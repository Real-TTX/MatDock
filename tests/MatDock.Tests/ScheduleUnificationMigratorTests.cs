using MatDock.Core.Entities;
using MatDock.Core.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatDock.Tests;

public class ScheduleUnificationMigratorTests
{
    private static ScheduleUnificationMigrator Migrator(TestDatabase db)
        => new(db.Context, NullLogger<ScheduleUnificationMigrator>.Instance);

    private static void Seed(TestDatabase db)
    {
        db.Context.BackupSchedules.Add(new BackupSchedule
        {
            Name = "nightly db", EnvironmentId = 1, Cron = "0 3 * * *", Enabled = true, VolumesCsv = "pgdata",
        });
        db.Context.SyncJobs.Add(new SyncJob
        {
            Name = "prod apps", GitRepoUrl = "https://example.com/repo.git", WebhookToken = "tok123",
            ScheduleEnabled = true, Cron = "0 4 * * *",
        });
        db.Context.SaveChanges();
    }

    [Fact]
    public async Task Migrates_backup_and_sync_into_scheduled_tasks()
    {
        using var db = new TestDatabase();
        Seed(db);

        var created = await Migrator(db).MigrateAsync();

        Assert.Equal(2, created);
        var tasks = await db.NewContext().ScheduledTasks.ToListAsync();
        var backup = Assert.Single(tasks, t => t.Action == ScheduleAction.Backup);
        Assert.Equal("backup", backup.SourceKind);
        Assert.Equal(ScheduleTrigger.Cron, backup.Trigger);
        Assert.NotNull(backup.NextRunAt);
        Assert.Equal(backup.SourceId, ScheduleOptions.Parse(backup.OptionsJson).BackupScheduleId);

        var sync = Assert.Single(tasks, t => t.Action == ScheduleAction.Sync);
        Assert.Equal("sync", sync.SourceKind);
        Assert.Equal(sync.SourceId, ScheduleOptions.Parse(sync.OptionsJson).SyncJobId);
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        using var db = new TestDatabase();
        Seed(db);

        Assert.Equal(2, await Migrator(db).MigrateAsync());
        Assert.Equal(0, await Migrator(db).MigrateAsync()); // second run creates nothing

        var count = await db.NewContext().ScheduledTasks.CountAsync();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Skips_sync_jobs_without_cron()
    {
        using var db = new TestDatabase();
        db.Context.SyncJobs.Add(new SyncJob
        {
            Name = "webhook only", GitRepoUrl = "https://example.com/r.git", WebhookToken = "t2",
            ScheduleEnabled = false, Cron = null,
        });
        db.Context.SaveChanges();

        Assert.Equal(0, await Migrator(db).MigrateAsync());
    }
}
