using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatDock.Web.Infrastructure;

/// <summary>Applies EF Core migrations and seeds the first administrator on an empty database.</summary>
public static class DbBootstrapper
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var db = provider.GetRequiredService<MatDockDbContext>();
        await db.Database.MigrateAsync();

        var users = provider.GetRequiredService<UserService>();
        if (await users.CountAsync() > 0)
        {
            return;
        }

        var admin = provider.GetRequiredService<IOptions<MatDockOptions>>().Value.Admin;
        await users.CreateAsync(
            admin.Username,
            admin.DisplayName,
            admin.Password,
            UserRole.Admin,
            mustChangePassword: true);

        logger.LogWarning(
            "Seeded initial administrator '{Username}'. The initial password must be changed on first login.",
            admin.Username);
    }
}
