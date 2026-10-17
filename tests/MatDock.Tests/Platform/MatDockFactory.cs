using System.Net;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Execution;
using MatDock.Core.Ssh;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MatDock.Tests.Platform;

/// <summary>
/// The whole app in memory, on its own empty data directory, without background services and without Docker: the hosts answer through
/// <see cref="Connection"/> and <see cref="Hosts"/> (a canned <c>docker ps</c>). <see cref="ApiKey"/> is <c>MAT_API_KEY</c> and <see cref="EngineKey"/>
/// the key of <c>/api/v1</c> (<c>MatDock__Api__Key</c>), each unless switched off; with an <c>authority</c> the sign-in through MatIdentity is on, with
/// a fixed OpenID configuration so that no MatIdentity has to run. Program reads its settings from environment variables, so the test classes run
/// one after the other (DisableTestParallelization). In Development MatDock seeds the administrator <c>tester</c> / <c>Tester123!</c>.
/// </summary>
public class MatDockFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "platform-key-0123456789";
    public const string EngineKey = "engine-key-0123456789";
    public const string Admin = "tester";
    public const string AdminPassword = "Tester123!";

    private static readonly string[] Variables =
    [
        "MatDock__DataPath", "MatDock__Api__Key", "MatDock__Admin__Seed", "MatDock__Environments__EnsureLocal",
        MatPlatformSettings.ApiKeyVariable, MatPlatformSettings.AuthorityVariable, MatPlatformSettings.ClientIdVariable,
        MatPlatformSettings.ClientSecretVariable, MatPlatformSettings.InternalUrlVariable,
    ];

    private readonly string _data = Path.Combine(Path.GetTempPath(), "matdock-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _authority;
    private readonly bool _reachable;
    private readonly bool _platformKey;
    private readonly bool _engineKey;

    public MatDockFactory()
        : this(platformKey: true, engineKey: true, authority: null)
    {
    }

    protected MatDockFactory(bool platformKey, bool engineKey, string? authority, bool reachable = true)
    {
        _platformKey = platformKey;
        _engineKey = engineKey;
        _authority = authority;
        _reachable = reachable;
        Directory.CreateDirectory(_data);
        SetVariables();
    }

    /// <summary>The Docker hosts as the platform tiles see them (images, disk, prune).</summary>
    public FakeConnectionService Connection { get; } = new();

    /// <summary>What <c>docker ps -a</c> answers on every host.</summary>
    public FakeHostSessionFactory Hosts { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Program reads them before the host is built; again, in case another factory changed them meanwhile.
        SetVariables();

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IEnvironmentConnectionService>();
            services.AddSingleton<IEnvironmentConnectionService>(Connection);
            services.RemoveAll<IHostSessionFactory>();
            services.AddSingleton<IHostSessionFactory>(Hosts);

            if (_authority is not null && _reachable)
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = _authority,
                    AuthorizationEndpoint = _authority + "/connect/authorize",
                    TokenEndpoint = _authority + "/connect/token",
                    EndSessionEndpoint = _authority + "/connect/logout",
                };

                services.PostConfigure<OpenIdConnectOptions>(MatIdentitySignIn.Scheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.PostConfigure<JwtBearerOptions>(MatApiAccess.BearerScheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            }
        });
    }

    /// <summary>A browser that keeps cookies and does not follow redirects (the tests look at every step).</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A browser that already has <paramref name="cookies"/>.</summary>
    public HttpClient Browser(CookieContainer cookies)
    {
        _ = Server;
        return new HttpClient(new CookieContainerHandler(cookies) { InnerHandler = Server.CreateHandler() }) { BaseAddress = new Uri("http://localhost") };
    }

    /// <summary>A caller with <paramref name="key"/> (default: <see cref="ApiKey"/>, like Mattiverse).</summary>
    public HttpClient Api(string key = ApiKey)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    /// <summary>Works with the database of the app (only in the tests; the web layer asks the services).</summary>
    public T Db<T>(Func<MatDockDbContext, T> work)
    {
        using var scope = Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<MatDockDbContext>());
    }

    /// <summary>An HttpContext with the services of the app, for <see cref="MatIdentitySignIn.CompleteSignInAsync"/> without a running MatIdentity.</summary>
    public static DefaultHttpContext Context(IServiceScope scope)
    {
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        return http;
    }

    /// <summary>A local environment (the host's Docker socket), online.</summary>
    public long AddLocalEnvironment(string name = "local")
        => Db(db =>
        {
            var env = new DockerEnvironment { Name = name, ConnectionType = ConnectionType.Local, Status = EnvironmentStatus.Online, IsEnabled = true };
            db.Environments.Add(env);
            db.SaveChanges();
            return env.Id;
        });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var variable in Variables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may hold the file a moment longer; the temp folder is cleaned up later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SetVariables()
    {
        Environment.SetEnvironmentVariable("MatDock__DataPath", _data);
        Environment.SetEnvironmentVariable("MatDock__Admin__Seed", null);
        Environment.SetEnvironmentVariable("MatDock__Environments__EnsureLocal", null);
        Environment.SetEnvironmentVariable("MatDock__Api__Key", _engineKey ? EngineKey : null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ApiKeyVariable, _platformKey ? ApiKey : null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.AuthorityVariable, _authority);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientIdVariable, null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientSecretVariable, _authority is null ? null : "test-secret");
        Environment.SetEnvironmentVariable(MatPlatformSettings.InternalUrlVariable, null);
    }
}

/// <summary>Without <c>MAT_API_KEY</c>, but with the engine key: the platform endpoints take the engine key.</summary>
public sealed class EngineKeyOnlyFactory() : MatDockFactory(platformKey: false, engineKey: true, authority: null);

/// <summary>Neither <c>MAT_API_KEY</c> nor an engine key: the protected part answers 401 to everyone, /api/v1 answers 503 as before.</summary>
public sealed class NoKeyFactory() : MatDockFactory(platformKey: false, engineKey: false, authority: null);

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> (a MatIdentity that does not have to run).</summary>
public sealed class IdentityFactory() : MatDockFactory(platformKey: true, engineKey: true, authority: Authority)
{
    public const string Authority = "http://identity.test";
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> at an address where nobody answers (MatIdentity is down).</summary>
public sealed class UnreachableIdentityFactory() : MatDockFactory(platformKey: true, engineKey: true, authority: "http://127.0.0.1:9", reachable: false);

/// <summary>Docker on every host, without Docker: <c>docker ps -a</c> answers <see cref="Containers"/>, everything else succeeds with no output.</summary>
public sealed class FakeHostSessionFactory : IHostSessionFactory
{
    /// <summary>The lines of <c>docker ps -a --format '{{json .}}'</c>.</summary>
    public List<string> Containers { get; } = [];

    public IHostSession Create(SshConnectionSettings settings) => new Session(this);

    private sealed class Session(FakeHostSessionFactory hosts) : IHostSession
    {
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public IHostCommand CreateCommand(string command)
            => new Command(command.Contains(" ps -a", StringComparison.Ordinal) ? string.Join("\n", hosts.Containers) : string.Empty);

        public void Dispose()
        {
        }
    }

    private sealed class Command(string result) : IHostCommand
    {
        public TimeSpan CommandTimeout { get; set; }

        public int? ExitStatus => 0;

        public string Error => string.Empty;

        public string Result => result;

        public Stream OutputStream { get; } = new MemoryStream();

        public bool BufferOutput { get; set; }

        public string Execute() => result;

        public Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;

        public IAsyncResult BeginExecute() => Task.CompletedTask;

        public Stream CreateInputStream() => new MemoryStream();

        public void EndExecute(IAsyncResult asyncResult)
        {
        }

        public void Dispose() => OutputStream.Dispose();
    }
}
