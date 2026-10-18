using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MatDock.Core.Data;

/// <summary>
/// The lock EF Core takes while it migrates the SQLite database (a row in <c>__EFMigrationsLock</c>). A process that is stopped in the middle of it
/// leaves the row behind, and every later start waits for it forever ("Acquiring an exclusive lock for migration application"). That happens on an
/// update: Mattiverse starts the stack again after the backup and recreates it seconds later. MatDock is the only process that opens its database, so a
/// row found before the migration is always stale and is removed.
/// </summary>
public static class MigrationLock
{
    public const string TableName = "__EFMigrationsLock";

    /// <summary>Removes a lock left behind by a stopped process; returns whether there was one.</summary>
    public static async Task<bool> ReleaseStaleAsync(DatabaseFacade database, CancellationToken ct = default)
    {
        if (!database.IsSqlite())
        {
            return false;
        }

        var connection = database.GetDbConnection();
        await database.OpenConnectionAsync(ct);
        try
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '" + TableName + "'";
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0)
            {
                return false;
            }

            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM \"" + TableName + "\"";
            return await delete.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }
}
