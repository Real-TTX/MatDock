using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatDock.Core.Data;

/// <summary>
/// EF Core context for the SQLite logic database. Enforces the project conventions centrally:
/// PascalCase singular table names, a BIGINT <c>Id</c> primary key, audit columns that are filled
/// automatically, and soft delete (a <c>Remove</c> becomes an <see cref="UpdateState.Deleted"/> update
/// and deleted rows are hidden by a global query filter).
/// </summary>
public class MatDockDbContext : DbContext
{
    private readonly ICurrentUserAccessor _currentUser;

    public MatDockDbContext(DbContextOptions<MatDockDbContext> options, ICurrentUserAccessor currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<DockerEnvironment> Environments => Set<DockerEnvironment>();
    public DbSet<VolumeBackup> VolumeBackups => Set<VolumeBackup>();
    public DbSet<BackupTarget> BackupTargets => Set<BackupTarget>();
    public DbSet<BackupSchedule> BackupSchedules => Set<BackupSchedule>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<Stack> Stacks => Set<Stack>();
    public DbSet<StackTemplate> StackTemplates => Set<StackTemplate>();
    public DbSet<GitCredential> GitCredentials => Set<GitCredential>();
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<SyncJobItem> SyncJobItems => Set<SyncJobItem>();
    public DbSet<GitRepo> GitRepos => Set<GitRepo>();
    public DbSet<ProxyConnection> ProxyConnections => Set<ProxyConnection>();
    public DbSet<ContainerRegistry> ContainerRegistries => Set<ContainerRegistry>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("User");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(128).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.Role).HasConversion<int>();
            // Unique only among non-deleted rows, so a username can be reused after a soft delete.
            e.HasIndex(x => x.Username).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasKey(x => x.Id);
            e.Property(x => x.Token).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User)
                .WithMany(u => u.Sessions)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<DockerEnvironment>(e =>
        {
            e.ToTable("Environment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Host).HasMaxLength(255).IsRequired();
            e.Property(x => x.Username).HasMaxLength(128).IsRequired();
            e.Property(x => x.AuthType).HasConversion<int>();
            e.Property(x => x.ConnectionType).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => x.Name);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<VolumeBackup>(e =>
        {
            e.ToTable("VolumeBackup");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceEnvironmentName).HasMaxLength(200);
            e.Property(x => x.VolumeName).HasMaxLength(255).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(300).IsRequired();
            e.HasIndex(x => x.VolumeName);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<BackupTarget>(e =>
        {
            e.ToTable("BackupTarget");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Type).HasConversion<int>();
            e.Property(x => x.SmbHost).HasMaxLength(255);
            e.Property(x => x.SmbShare).HasMaxLength(255);
            e.Property(x => x.SmbDirectory).HasMaxLength(500);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<BackupSchedule>(e =>
        {
            e.ToTable("BackupSchedule");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Cron).HasMaxLength(120).IsRequired();
            e.Ignore(x => x.Volumes);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<Stack>(e =>
        {
            e.ToTable("Stack");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.LastStatus).HasMaxLength(200);
            // The name is the compose project key (host dir + `-p <name>`), unique per environment.
            // Filtered so a name can be reused after a soft delete (same pattern as User.Username).
            e.HasIndex(x => new { x.Name, x.EnvironmentId }).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasIndex(x => x.EnvironmentId);
            e.Property(x => x.GitRepoUrl).HasMaxLength(1000);
            e.Property(x => x.GitReference).HasMaxLength(200);
            e.Property(x => x.GitComposePath).HasMaxLength(500);
            e.HasIndex(x => x.SyncJobId);
            e.Ignore(x => x.IsGitBacked);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<StackTemplate>(e =>
        {
            e.ToTable("StackTemplate");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.Name);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<GitCredential>(e =>
        {
            e.ToTable("GitCredential");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.AuthType).HasConversion<int>();
            e.Property(x => x.Username).HasMaxLength(200);
            e.HasIndex(x => x.Name);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<SyncJob>(e =>
        {
            e.ToTable("SyncJob");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.GitRepoUrl).HasMaxLength(1000).IsRequired();
            e.Property(x => x.GitReference).HasMaxLength(200);
            e.Property(x => x.Subdirectory).HasMaxLength(500);
            e.Property(x => x.Cron).HasMaxLength(120);
            e.Property(x => x.UpdateMode).HasConversion<int>();
            e.Property(x => x.WebhookToken).HasMaxLength(64).IsRequired();
            e.Property(x => x.LastCommitSha).HasMaxLength(64);
            e.Property(x => x.LastStatus).HasMaxLength(400);
            // The webhook token is the URL secret; look it up by equality, so keep it unique.
            e.HasIndex(x => x.WebhookToken).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<SyncJobItem>(e =>
        {
            e.ToTable("SyncJobItem");
            e.HasKey(x => x.Id);
            e.Property(x => x.ComposePath).HasMaxLength(500).IsRequired();
            e.Property(x => x.StackName).HasMaxLength(200).IsRequired();
            e.Property(x => x.LastStatus).HasMaxLength(400);
            e.HasIndex(x => x.SyncJobId);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<GitRepo>(e =>
        {
            e.ToTable("GitRepo");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Url).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Reference).HasMaxLength(200);
            e.HasIndex(x => x.Name);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<ProxyConnection>(e =>
        {
            e.ToTable("ProxyConnection");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Provider).HasConversion<int>();
            e.Property(x => x.Url).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ServerName).HasMaxLength(200);
            e.HasIndex(x => x.Name);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<ContainerRegistry>(e =>
        {
            e.ToTable("ContainerRegistry");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Host).HasMaxLength(255).IsRequired();
            e.Property(x => x.Username).HasMaxLength(200);
            e.HasIndex(x => x.Name);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<ScheduledTask>(e =>
        {
            e.ToTable("ScheduledTask");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Trigger).HasConversion<int>();
            e.Property(x => x.Action).HasConversion<int>();
            e.Property(x => x.Event).HasConversion<int>();
            e.Property(x => x.Cron).HasMaxLength(120);
            e.Property(x => x.OptionsJson).HasMaxLength(2000);
            e.Property(x => x.LastStatus).HasMaxLength(1000);
            e.Property(x => x.SourceKind).HasMaxLength(20);
            e.HasIndex(x => x.Name);
            e.HasIndex(x => new { x.SourceKind, x.SourceId });
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills audit columns and turns hard deletes into soft deletes.</summary>
    private void ApplyAudit()
    {
        var now = DateTime.UtcNow;
        var userId = _currentUser.UserId;

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreateDate = now;
                    entry.Entity.CreateUserId = userId;
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    entry.Entity.UpdateState = UpdateState.Created;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    if (entry.Entity.UpdateState != UpdateState.Deleted)
                    {
                        entry.Entity.UpdateState = UpdateState.Updated;
                    }
                    break;

                case EntityState.Deleted:
                    // Soft delete: keep the row, flag it and hide it via the query filter.
                    entry.State = EntityState.Modified;
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    entry.Entity.UpdateState = UpdateState.Deleted;
                    break;
            }
        }
    }
}
