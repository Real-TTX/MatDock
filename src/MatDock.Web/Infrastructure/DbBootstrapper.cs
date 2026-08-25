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
        var options = provider.GetRequiredService<IOptions<MatDockOptions>>().Value;

        // Seed the initial administrator only on a fresh database.
        if (await users.CountAsync() == 0)
        {
            var admin = options.Admin;
            await users.CreateAsync(admin.Username, admin.DisplayName, admin.Password, UserRole.Admin, mustChangePassword: true);
            logger.LogWarning(
                "Seeded initial administrator '{Username}'. The initial password must be changed on first login.",
                admin.Username);
        }

        // Dev-only: seed a ready-to-use test account so testing never touches the real admin.
        if (options.SeedTestUser)
        {
            var test = options.TestUser;
            if (!await users.UsernameExistsAsync(test.Username))
            {
                await users.CreateAsync(test.Username, test.DisplayName, test.Password, UserRole.Admin, mustChangePassword: false);
                logger.LogWarning("Seeded DEV test account '{Username}'. Do not enable MatDock:SeedTestUser in production.", test.Username);
            }
        }
    }
}
