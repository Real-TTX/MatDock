using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatDock.Tests;

public class AuditSoftDeleteTests
{
    [Fact]
    public void Insert_stamps_create_audit_fields()
    {
        using var db = new TestDatabase(currentUserId: 42);
        var env = new DockerEnvironment { Name = "A", Host = "h", Username = "u" };

        db.Context.Environments.Add(env);
        db.Context.SaveChanges();

        Assert.True(env.Id > 0);
        Assert.Equal(UpdateState.Created, env.UpdateState);
        Assert.Equal(42, env.CreateUserId);
        Assert.Equal(42, env.UpdateUserId);
        Assert.NotEqual(default, env.CreateDate);
    }

    [Fact]
    public void Update_sets_state_updated()
    {
        using var db = new TestDatabase();
        var env = new DockerEnvironment { Name = "A", Host = "h", Username = "u" };
        db.Context.Environments.Add(env);
        db.Context.SaveChanges();

        env.Name = "B";
        db.Context.SaveChanges();

        Assert.Equal(UpdateState.Updated, env.UpdateState);
    }

    [Fact]
    public void Remove_becomes_soft_delete_and_is_filtered_out()
    {
        using var db = new TestDatabase();
        var env = new DockerEnvironment { Name = "A", Host = "h", Username = "u" };
        db.Context.Environments.Add(env);
        db.Context.SaveChanges();

        db.Context.Environments.Remove(env);
        db.Context.SaveChanges();

        // Hidden from normal queries...
        Assert.Empty(db.Context.Environments.ToList());

        // ...but the row still exists and is flagged Deleted.
        var raw = db.Context.Environments.IgnoreQueryFilters().Single();
        Assert.Equal(UpdateState.Deleted, raw.UpdateState);
    }

    [Fact]
    public void Unique_username_is_scoped_to_non_deleted_rows()
    {
        using var db = new TestDatabase();

        var first = new User { Username = "same", DisplayName = "One", PasswordHash = "x" };
        db.Context.Users.Add(first);
        db.Context.SaveChanges();

        db.Context.Users.Remove(first);
        db.Context.SaveChanges();

        // Reusing the username after a soft delete must be allowed.
        db.Context.Users.Add(new User { Username = "same", DisplayName = "Two", PasswordHash = "y" });
        var ex = Record.Exception(() => db.Context.SaveChanges());
        Assert.Null(ex);
    }
}
