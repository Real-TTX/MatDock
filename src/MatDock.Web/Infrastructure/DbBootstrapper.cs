using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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
        var environment = provider.GetRequiredService<IHostEnvironment>();

        // Seed the initial administrator only on a fresh database.
        if (await users.CountAsync() == 0)
        {
            var admin = options.Admin;
            await users.CreateAsync(admin.Username, admin.DisplayName, admin.Password, UserRole.Admin, mustChangePassword: true);
            logger.LogWarning(
                "Seeded initial administrator '{Username}'. The initial password must be changed on first login.",
                admin.Username);
        }

        // Local/dev only (never in Production): a ready-to-use test account so testing never touches
        // the real admin. Automatic — no configuration flag needed.
        if (environment.IsDevelopment() && !await users.UsernameExistsAsync("tester"))
        {
            await users.CreateAsync("tester", "Test-Benutzer", "Tester123!", UserRole.Admin, mustChangePassword: false);
            logger.LogWarning("Seeded local test account 'tester' / 'Tester123!' (Development only).");
        }
    }
}
