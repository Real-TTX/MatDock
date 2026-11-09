using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatDock.Tests.Platform;

/// <summary>
/// SSH environments by the API: created by name with a generated key pair (only the public key leaves MatDock), changed without losing the key,
/// a new key on request or for another endpoint, tested again by id or name; a read key may not, a name of another kind is a conflict.
/// </summary>
public sealed class EnvironmentsApiTests
{
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.DoesNotContain("PRIVATE KEY", text);
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task A_new_environment_gets_a_generated_key_and_answers_only_its_public_key()
    {
        using var site = new MatDockFactory();
        var api = site.Api(MatDockFactory.EngineKey);

        var json = await ReadAsync(await api.PutAsync("/api/v1/environments/ssh/server1",
            Json(new { host = "server1.example.com", username = "adminms", description = "Hetzner" })));

        Assert.True(json.GetProperty("created").GetBoolean());
        var publicKey = json.GetProperty("publicKey").GetString()!;
        Assert.StartsWith("ssh-rsa ", publicKey);
        Assert.EndsWith(" matdock@server1.example.com", publicKey);
        Assert.True(json.GetProperty("connected").GetBoolean());
        Assert.Equal("27.0", json.GetProperty("dockerVersion").GetString());

        var env = site.Db(db => db.Environments.Single(e => e.Name == "server1"));
        Assert.Equal((ConnectionType.Ssh, AuthType.PrivateKey, "server1.example.com", 22, "adminms", "Hetzner"),
            (env.ConnectionType, env.AuthType, env.Host, env.Port, env.Username, env.Description));
        Assert.NotNull(env.EncryptedPrivateKey);
        Assert.DoesNotContain("PRIVATE KEY", env.EncryptedPrivateKey);
        Assert.Contains("BEGIN RSA PRIVATE KEY", site.Connection.LastSettings!.PrivateKeyPem);
        Assert.Equal("server1.example.com", site.Connection.LastSettings.Host);
    }

    [Fact]
    public async Task A_change_keeps_the_key_unless_a_new_one_is_asked_for_or_the_endpoint_changes()
    {
        using var site = new MatDockFactory();
        var api = site.Api(MatDockFactory.EngineKey);
        var first = await ReadAsync(await api.PutAsync("/api/v1/environments/ssh/srv", Json(new { host = "srv.example.com", username = "admin" })));
        var key = site.Connection.LastSettings!.PrivateKeyPem;

        var same = await ReadAsync(await api.PutAsync("/api/v1/environments/ssh/SRV", Json(new { host = "srv.example.com", username = "admin", baseUrl = "https://srv.example.com" })));
        Assert.False(same.GetProperty("created").GetBoolean());
        Assert.Equal(first.GetProperty("id").GetInt64(), same.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, same.GetProperty("publicKey").ValueKind);
        Assert.Equal(key, site.Connection.LastSettings!.PrivateKeyPem);
        Assert.Equal("https://srv.example.com", site.Db(db => db.Environments.Single(e => e.Name == "srv").BaseUrl));

        var renewed = await ReadAsync(await api.PutAsync("/api/v1/environments/ssh/srv", Json(new { host = "srv.example.com", username = "admin", generateKey = true })));
        Assert.NotEqual(first.GetProperty("publicKey").GetString(), renewed.GetProperty("publicKey").GetString());
        Assert.NotEqual(key, site.Connection.LastSettings!.PrivateKeyPem);

        var moved = await ReadAsync(await api.PutAsync("/api/v1/environments/ssh/srv", Json(new { host = "srv2.example.com", username = "admin" })));
        Assert.EndsWith(" matdock@srv2.example.com", moved.GetProperty("publicKey").GetString());
        Assert.Equal(1, site.Db(db => db.Environments.Count(e => e.ConnectionType == ConnectionType.Ssh)));
    }

    [Fact]
    public async Task A_given_key_is_used_and_not_answered()
    {
        using var site = new MatDockFactory();
        var pem = MatDock.Core.Ssh.SshKeygen.Generate().PrivateKeyPem;

        var json = await ReadAsync(await site.Api(MatDockFactory.EngineKey).PutAsync("/api/v1/environments/ssh/given",
            Json(new { host = "10.0.0.5", port = 2222, username = "ops", privateKeyPem = pem })));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("publicKey").ValueKind);
        Assert.Equal((2222, pem.Trim()), (site.Connection.LastSettings!.Port, site.Connection.LastSettings.PrivateKeyPem!.Trim()));
    }

    [Fact]
    public async Task The_test_by_name_or_id_stores_the_host_key()
    {
        using var site = new MatDockFactory();
        var api = site.Api(MatDockFactory.EngineKey);
        var id = (await ReadAsync(await api.PutAsync("/api/v1/environments/ssh/tested", Json(new { host = "t.example.com", username = "u" })))).GetProperty("id").GetInt64();
        site.Db(db =>
        {
            db.Environments.Single(e => e.Id == id).HostKeyFingerprint = null;
            return db.SaveChanges();
        });
        site.Connection.HostKey = "SHA256:abcdef";

        var byName = await ReadAsync(await api.PostAsync("/api/v1/environments/Tested/test", null));
        Assert.True(byName.GetProperty("connected").GetBoolean());
        Assert.Equal("SHA256:abcdef", byName.GetProperty("hostKey").GetString());
        Assert.Equal(id, (await ReadAsync(await api.PostAsync($"/api/v1/environments/{id}/test", null))).GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync("/api/v1/environments/nope/test", null)).StatusCode);
    }

    [Fact]
    public async Task Wrong_input_a_read_key_and_another_kind_of_environment_are_refused()
    {
        using var site = new MatDockFactory();
        site.AddLocalEnvironment("local");
        var api = site.Api(MatDockFactory.EngineKey);

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/v1/environments/ssh/x", Json(new { username = "u" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/v1/environments/ssh/x", Json(new { host = "h", username = "a b" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/v1/environments/ssh/x", Json(new { host = "h", username = "u", port = 70000 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PutAsync("/api/v1/environments/ssh/LOCAL", Json(new { host = "h", username = "u" }))).StatusCode);

        string readKey;
        using (var scope = site.Services.CreateScope())
        {
            (_, _, readKey) = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync("reader", ApiKeyScope.Read, null);
        }

        var reader = site.Browser();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readKey!);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync("/api/v1/environments/ssh/x", Json(new { host = "h", username = "u" }))).StatusCode);
        Assert.Equal(0, site.Db(db => db.Environments.Count(e => e.ConnectionType == ConnectionType.Ssh)));
    }
}
