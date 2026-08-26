using MatDock.Core.Auth;
using MatDock.Core.Environments;
using MatDock.Core.Security;
using MatDock.Core.Ssh;
using MatDock.Core.Users;
using Microsoft.Extensions.DependencyInjection;

namespace MatDock.Core;

/// <summary>
/// Registers the MatDock domain/services. The hosting layer is still responsible for the
/// infrastructure that needs configuration: the <c>MatDockDbContext</c> (connection string),
/// Data Protection, options binding, <c>AppPaths</c> and <c>ICurrentUserAccessor</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMatDockCore(this IServiceCollection services)
    {
        // Stateless helpers.
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<ISshClientFactory, SshNetClientFactory>();
        services.AddSingleton<Backups.IBackupStorageFactory, Backups.BackupStorageFactory>();

        // Per-request services (depend on the scoped DbContext).
        services.AddScoped<AuthService>();
        services.AddScoped<SessionService>();
        services.AddScoped<UserService>();
        services.AddScoped<EnvironmentService>();
        services.AddScoped<IEnvironmentConnectionService, EnvironmentConnectionService>();
        services.AddScoped<Volumes.VolumeMigrationService>();
        services.AddScoped<Volumes.VolumeBackupService>();
        services.AddScoped<Backups.BackupTargetService>();
        services.AddScoped<Backups.BackupScheduleRunner>();
        services.AddScoped<Backups.BackupScheduleService>();
        services.AddScoped<Containers.ContainerService>();
        services.AddScoped<Notifications.NotificationSettingsService>();
        services.AddScoped<Notifications.INotificationService, Notifications.NotificationService>();
        services.AddHttpClient();

        return services;
    }
}
