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
        services.AddSingleton<Execution.IHostSessionFactory, Execution.HostSessionFactory>();
        services.AddSingleton<Backups.IBackupStorageFactory, Backups.BackupStorageFactory>();

        // Per-request services (depend on the scoped DbContext).
        services.AddScoped<AuthService>();
        services.AddScoped<SessionService>();
        services.AddScoped<UserService>();
        services.AddScoped<EnvironmentService>();
        services.AddScoped<IEnvironmentConnectionService, EnvironmentConnectionService>();
        services.AddScoped<Volumes.VolumeMigrationService>();
        services.AddScoped<Volumes.VolumeBackupService>();
        services.AddScoped<Volumes.VolumeFileService>();
        services.AddScoped<Backups.BackupTargetService>();
        services.AddScoped<Backups.BundleBackupService>();
        services.AddScoped<Backups.BackupScheduleRunner>();
        services.AddScoped<Backups.BackupScheduleService>();
        services.AddScoped<Containers.ContainerService>();
        services.AddScoped<Stacks.StackService>();
        services.AddScoped<Templates.StackTemplateService>();
        services.AddScoped<Git.GitCredentialService>();
        services.AddSingleton<Git.GitRepositoryService>();
        services.AddScoped<Git.GitRepoService>();
        services.AddScoped<Sync.SyncJobRunner>();
        services.AddScoped<Sync.SyncJobService>();
        services.AddScoped<Notifications.NotificationSettingsService>();
        services.AddScoped<Notifications.INotificationService, Notifications.NotificationService>();
        services.AddSingleton<Proxies.IProxyProvider, Proxies.CaddyProxyProvider>();
        services.AddSingleton<Proxies.IProxyProvider, Proxies.MatcadProxyProvider>();
        services.AddScoped<Proxies.ProxyConnectionService>();
        services.AddSingleton<Registries.RegistryApiClient>();
        services.AddScoped<Registries.RegistryService>();
        services.AddScoped<Schedules.IScheduleAction, Schedules.PruneVolumesAction>();
        services.AddScoped<Schedules.IScheduleAction, Schedules.PruneImagesAction>();
        services.AddScoped<Schedules.IScheduleAction, Schedules.PruneNetworksAction>();
        services.AddScoped<Schedules.IScheduleAction, Schedules.SummaryAction>();
        services.AddScoped<Schedules.ScheduleService>();
        services.AddHttpClient();

        return services;
    }
}
