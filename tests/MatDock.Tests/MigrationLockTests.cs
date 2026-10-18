using MatDock.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Tests;

/// <summary>
/// A process stopped while it migrates leaves EF Core's lock row behind, and the next start would wait for it forever (it happened on an update:
/// the stack is started again after the backup and recreated seconds later).
/// </summary>
public class MigrationLockTests
{
    [Fact]
    public async Task A_lock_left_behind_is_removed_and_the_migration_runs()
    {
        var file = Path.Combine(Path.GetTempPath(), "matdock-lock-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using (var connection = new SqliteConnection("Data Source=" + file))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE \"__EFMigrationsLock\" (\"Id\" INTEGER NOT NULL PRIMARY KEY, \"Timestamp\" TEXT NOT NULL); " +
                                      "INSERT INTO \"__EFMigrationsLock\" VALUES (1, '2026-10-18 13:28:48');";
                await command.ExecuteNonQueryAsync();
            }

            await using var db = Context(file);
            Assert.True(await MigrationLock.ReleaseStaleAsync(db.Database));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await db.Database.MigrateAsync(timeout.Token);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(await MigrationLock.ReleaseStaleAsync(db.Database));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_new_database_has_nothing_to_release()
    {
        var file = Path.Combine(Path.GetTempPath(), "matdock-lock-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = Context(file);
            Assert.False(await MigrationLock.ReleaseStaleAsync(db.Database));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    private static MatDockDbContext Context(string file)
        => new(new DbContextOptionsBuilder<MatDockDbContext>().UseSqlite("Data Source=" + file).Options, new FakeCurrentUser(1));
}
