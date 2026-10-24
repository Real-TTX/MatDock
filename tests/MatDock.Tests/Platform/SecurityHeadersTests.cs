using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>Every answer carries the headers that keep the browser on MatDock's side: pages, static files and the API alike.</summary>
public sealed class SecurityHeadersTests(MatDockFactory site) : IClassFixture<MatDockFactory>
{
    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/robots.txt")]
    [InlineData("/api/health")]
    public async Task An_answer_cannot_be_framed_sniffed_or_load_foreign_scripts(string path)
    {
        var response = await site.Browser().GetAsync(path);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("connect-src 'self' ws://localhost wss://localhost", csp); // the log and terminal WebSockets of this host
        Assert.DoesNotContain("'unsafe-eval'", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
    }
}

/// <summary>The app as it runs: a stack's .env that comes in through the API lands encrypted in the database and goes out plain through the API again.</summary>
public sealed class SecretsInTheAppTests : IDisposable
{
    private readonly MatDockFactory _site = new();

    public void Dispose() => _site.Dispose();

    [Fact]
    public async Task The_env_of_a_stack_is_encrypted_in_the_database_of_the_running_app()
    {
        _site.AddLocalEnvironment();
        var api = _site.Api(MatDockFactory.EngineKey);
        var put = await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(api, "/api/v1/stacks/vault",
            new { composeYaml = "services:\n  web:\n    image: nginx\n", envContent = "DB_PASSWORD=hunter2" });
        put.EnsureSuccessStatusCode();

        var raw = _site.Db(db =>
        {
            var connection = db.Database.GetDbConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"EnvContent\" FROM \"Stack\" WHERE \"Name\" = 'vault'";
            return command.ExecuteScalar() as string;
        });
        Assert.StartsWith(MatDock.Core.Security.StackSecretsInterceptor.Prefix, raw);
        Assert.DoesNotContain("hunter2", raw);

        var detail = await api.GetStringAsync("/api/v1/stacks/vault");
        Assert.Contains("DB_PASSWORD=hunter2", detail); // Mattiverse reads what it wrote
    }
}
