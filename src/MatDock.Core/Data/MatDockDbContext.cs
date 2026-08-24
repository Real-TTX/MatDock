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
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => x.Name);
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
