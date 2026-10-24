using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatDock.Tests;

/// <summary>The <c>.env</c> of a stack (passwords, keys) is encrypted in the database; MatDock itself reads the plain text as before.</summary>
public class SecretsAtRestTests
{
    private const string Env = "DB_PASSWORD=hunter2\nTZ=Europe/Berlin";

    private static StackSecretsInterceptor Interceptor(IDataProtectionProvider? provider = null)
        => new(new DataProtectionSecretProtector(provider ?? new EphemeralDataProtectionProvider()));

    private static string RawEnv(TestDatabase db, long id)
    {
        using var context = db.NewContext();
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"EnvContent\" FROM \"Stack\" WHERE \"Id\" = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    private static long Add(TestDatabase db, string? env)
    {
        var stack = new Stack { Name = "shop", EnvironmentId = 1, ComposeYaml = "services: {}", EnvContent = env };
        db.Context.Stacks.Add(stack);
        db.Context.SaveChanges();
        return stack.Id;
    }

    [Fact]
    public void The_env_is_encrypted_in_the_database_and_plain_for_the_code()
    {
        using var db = new TestDatabase(1, Interceptor());
        var id = Add(db, Env);

        var raw = RawEnv(db, id);
        Assert.StartsWith(StackSecretsInterceptor.Prefix, raw);
        Assert.DoesNotContain("hunter2", raw);

        Assert.Equal(Env, db.Context.Stacks.Single(s => s.Id == id).EnvContent); // the tracked entity keeps the plain text after the save
        using var fresh = db.NewContext();
        Assert.Equal(Env, fresh.Stacks.AsNoTracking().Single(s => s.Id == id).EnvContent);
        Assert.Equal(Env, fresh.Stacks.Single(s => s.Id == id).EnvContent);
    }

    [Fact]
    public void A_stack_that_was_only_read_is_not_written_again()
    {
        using var db = new TestDatabase(1, Interceptor());
        var id = Add(db, Env);
        var before = RawEnv(db, id);

        using var context = db.NewContext();
        var stack = context.Stacks.Single(s => s.Id == id);
        Assert.Equal(EntityState.Unchanged, context.Entry(stack).State); // reading decrypts without marking it changed
        context.SaveChanges();

        Assert.Equal(before, RawEnv(db, id));
    }

    [Fact]
    public void Changing_the_env_writes_the_new_value_encrypted()
    {
        using var db = new TestDatabase(1, Interceptor());
        var id = Add(db, Env);

        using (var context = db.NewContext())
        {
            context.Stacks.Single(s => s.Id == id).EnvContent = "DB_PASSWORD=new-secret";
            context.SaveChanges();
        }

        Assert.DoesNotContain("new-secret", RawEnv(db, id));
        using var fresh = db.NewContext();
        Assert.Equal("DB_PASSWORD=new-secret", fresh.Stacks.Single(s => s.Id == id).EnvContent);
    }

    [Fact]
    public async Task A_plain_env_of_an_older_MatDock_is_read_as_it_is_and_encrypted_at_the_start()
    {
        using var db = new TestDatabase(1); // written without the interceptor: plain
        var id = Add(db, Env);
        Assert.Equal(Env, RawEnv(db, id));

        using var context = new MatDock.Core.Data.MatDockDbContext(
            new DbContextOptionsBuilder<MatDock.Core.Data.MatDockDbContext>().UseSqlite(db.Context.Database.GetDbConnection()).AddInterceptors(Interceptor()).Options,
            new FakeCurrentUser(1));
        Assert.Equal(Env, context.Stacks.AsNoTracking().Single(s => s.Id == id).EnvContent);

        Assert.Equal(1, await StackSecretsInterceptor.EncryptExistingAsync(context));
        Assert.StartsWith(StackSecretsInterceptor.Prefix, RawEnv(db, id));
        Assert.Equal(0, await StackSecretsInterceptor.EncryptExistingAsync(context)); // nothing left
    }

    [Fact]
    public void Empty_and_missing_env_stay_as_they_are()
    {
        using var db = new TestDatabase(1, Interceptor());
        Assert.Equal(string.Empty, RawEnv(db, Add(db, null)));
    }

    [Fact]
    public void A_value_from_other_keys_cannot_be_read()
    {
        var mine = Interceptor(new EphemeralDataProtectionProvider());
        var other = Interceptor(new EphemeralDataProtectionProvider());
        var sealedValue = mine.Protect(Env);

        Assert.Equal(Env, mine.Unprotect(sealedValue));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => other.Unprotect(sealedValue));
        Assert.Equal("plain", other.Unprotect("plain"));
    }
}
