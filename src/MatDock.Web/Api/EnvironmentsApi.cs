using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Ssh;

namespace MatDock.Web.Api;

/// <summary>Body of <c>PUT /api/v1/environments/ssh/{name}</c>.</summary>
public sealed class SshEnvironmentBody
{
    public string? Host { get; set; }

    /// <summary>Left out = 22 (a new environment) or the port it has.</summary>
    public int? Port { get; set; }

    public string? Username { get; set; }

    /// <summary>Left out = the one it has.</summary>
    public string? Description { get; set; }

    /// <summary>Left out = the one it has.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>A private key (PEM) to log in with. Left out on a new environment: MatDock generates a key pair and answers its public key.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>true = a new key pair for an existing environment too (its old key stays valid on the host until removed there).</summary>
    public bool GenerateKey { get; set; }
}

/// <summary>
/// <c>/api/v1</c> for adding hosts without the page: an SSH environment by name (created or changed, the same service as the page) and a
/// connection test. The private key never leaves MatDock; a generated one is answered as its public key to put into <c>authorized_keys</c>.
/// </summary>
public static class EnvironmentsApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPut("/environments/ssh/{name}", PutSshAsync)
            .WithSummary("Creates or changes an SSH environment by name; without a key MatDock generates one and answers the public key to install on the host.");
        api.MapPost("/environments/{selector}/test", TestAsync)
            .WithSummary("Connects to an environment (id or name) again and stores its status, Docker version and, the first time, its host key.");
    }

    private static async Task<IResult> PutSshAsync(string name, HttpContext http, EnvironmentService environments)
    {
        var (body, error) = await ApiResults.ReadBodyAsync<SshEnvironmentBody>(http.Request);
        if (body is null)
        {
            return error!;
        }

        name = name.Trim();
        var host = body.Host?.Trim() ?? string.Empty;
        var user = body.Username?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
        {
            return ApiResults.BadRequest("The name must have 1 to 200 characters.");
        }

        if (host.Length is 0 or > 255 || host.Any(char.IsWhiteSpace))
        {
            return ApiResults.BadRequest("host is required (a name or address without spaces).");
        }

        if (user.Length is 0 or > 128 || user.Any(char.IsWhiteSpace))
        {
            return ApiResults.BadRequest("username is required (without spaces).");
        }

        var ct = http.RequestAborted;
        var existing = (await environments.GetAllAsync(ct))
            .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.ConnectionType != ConnectionType.Ssh)
        {
            return ApiResults.Conflict($"Environment '{existing.Name}' exists and is not an SSH environment.");
        }

        var port = body.Port ?? existing?.Port ?? 22;
        if (port is < 1 or > 65535)
        {
            return ApiResults.BadRequest("port must be between 1 and 65535.");
        }

        var privateKey = string.IsNullOrWhiteSpace(body.PrivateKeyPem) ? null : body.PrivateKeyPem.Trim() + "\n";
        string? publicKey = null;
        // The stored key is kept only for the same endpoint: the service drops a secret when host, port or user change.
        var keepsKey = existing is not null && existing.AuthType == AuthType.PrivateKey && existing.EncryptedPrivateKey is not null
            && string.Equals(existing.Host, host, StringComparison.OrdinalIgnoreCase) && existing.Port == port
            && string.Equals(existing.Username, user, StringComparison.Ordinal);
        if (privateKey is null && (body.GenerateKey || !keepsKey))
        {
            var pair = SshKeygen.Generate($"matdock@{host}");
            privateKey = pair.PrivateKeyPem;
            publicKey = pair.PublicKeyOpenSsh;
        }

        var input = new EnvironmentInput
        {
            Name = existing?.Name ?? name,
            Description = body.Description ?? existing?.Description,
            BaseUrl = body.BaseUrl ?? existing?.BaseUrl,
            ConnectionType = ConnectionType.Ssh,
            Host = host,
            Port = port,
            Username = user,
            AuthType = AuthType.PrivateKey,
            PrivateKeyPem = privateKey, // null keeps the stored key
        };

        long id;
        if (existing is null)
        {
            id = (await environments.CreateAsync(input, ct)).Id;
        }
        else
        {
            await environments.UpdateAsync(existing.Id, input, ct);
            id = existing.Id;
        }

        var test = await environments.TestAndPersistAsync(id, ct);
        return ApiResults.Ok(new
        {
            id,
            name = input.Name,
            created = existing is null,
            publicKey,
            connected = test.Success,
            status = test.Status.ToString().ToLowerInvariant(),
            dockerVersion = test.DockerVersion,
            message = test.Message,
            hint = publicKey is not null && !test.Success
                ? "Put publicKey into ~/.ssh/authorized_keys of the user on the host, then call POST /api/v1/environments/" + id + "/test."
                : null,
        });
    }

    private static async Task<IResult> TestAsync(string selector, HttpContext http, EnvironmentService environments)
    {
        var ct = http.RequestAborted;
        var all = await environments.GetAllAsync(ct);
        var env = long.TryParse(selector, out var id) ? all.FirstOrDefault(e => e.Id == id) : null;
        env ??= all.FirstOrDefault(e => string.Equals(e.Name, selector.Trim(), StringComparison.OrdinalIgnoreCase));
        if (env is null)
        {
            return ApiResults.NotFound($"Environment '{selector}' not found.");
        }

        var test = await environments.TestAndPersistAsync(env.Id, ct);
        var stored = await environments.GetAsync(env.Id, ct);
        return ApiResults.Ok(new
        {
            id = env.Id,
            name = env.Name,
            connected = test.Success,
            status = test.Status.ToString().ToLowerInvariant(),
            dockerVersion = test.DockerVersion,
            access = test.AccessLabel,
            hostKey = stored?.HostKeyFingerprint,
            message = test.Message,
        });
    }
}
