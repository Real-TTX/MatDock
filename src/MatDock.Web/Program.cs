using MatDock.Core;
using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Web.Api;
using MatDock.Web.Infrastructure;
using MatDock.Web.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration / options
// ---------------------------------------------------------------------------
builder.Services.Configure<MatDockOptions>(builder.Configuration.GetSection(MatDockOptions.SectionName));

// Resolve the data directory before the container is built (needed for keys + SQLite path).
// In Docker the compose/Dockerfile sets MatDock__DataPath=/data explicitly; the standalone EXE and
// local runs fall back to an App_Data folder next to the application.
var configuredDataPath = builder.Configuration[$"{MatDockOptions.SectionName}:DataPath"];
var dataPath = !string.IsNullOrWhiteSpace(configuredDataPath)
    ? configuredDataPath
    : Path.Combine(builder.Environment.ContentRootPath, "App_Data");

var appPaths = new AppPaths(dataPath);
appPaths.EnsureCreated();
builder.Services.AddSingleton(appPaths);
builder.Services.PostConfigure<MatDockOptions>(options => options.DataPath = dataPath);

var sessionLifetimeDays = builder.Configuration.GetValue($"{MatDockOptions.SectionName}:SessionLifetimeDays", 30);

// ---------------------------------------------------------------------------
// Infrastructure services
// ---------------------------------------------------------------------------

// Data Protection keys live on the mounted volume so cookies + encrypted secrets survive restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("MatDock")
    .PersistKeysToFileSystem(new DirectoryInfo(appPaths.KeysPath));

builder.Services.AddDbContext<MatDockDbContext>(options =>
    options.UseSqlite(appPaths.SqliteConnectionString));

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ICurrentUserAccessor, HttpCurrentUserAccessor>();
builder.Services.AddSingleton<MatDock.Web.Infrastructure.SetupState>();

// Honour X-Forwarded-* from a TLS-terminating reverse proxy so Request.IsHttps is correct and the
// auth cookie is marked Secure when the client connection is HTTPS.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddMatDockCore();
builder.Services.AddScoped<MatDock.Web.Support.AppLaunchpadService>();
// Every timed task (backups, bundles, sync jobs, prune, reports) runs through the Schedules; webhooks start syncs directly.
builder.Services.AddHostedService<ScheduleRunnerService>();
builder.Services.AddHostedService<EnvironmentHealthMonitor>();
builder.Services.AddHostedService<ContainerAlertMonitor>();

// ---------------------------------------------------------------------------
// The Mat platform contract (MatDesign docs/PLATFORM.md): /api, /api/health, /api/state, /api/configure, /api/actions
// (the package MatPlatform, a copy in MatPlatform/; DockPlatform describes MatDock and supplies the values), and the optional
// sign-in through MatIdentity (MATIDENTITY_*). The key of the platform endpoints is MAT_API_KEY; without it the engine key
// of /api/v1 (MatDock__Api__Key) is the key there too, so one key serves a caller that only knows the engine key.
// ---------------------------------------------------------------------------
var engineKey = builder.Configuration[$"{MatDockOptions.SectionName}:Api:Key"];
if (string.IsNullOrWhiteSpace(builder.Configuration[MatPlatformSettings.ApiKeyVariable]) && !string.IsNullOrWhiteSpace(engineKey))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [MatPlatformSettings.ApiKeyVariable] = engineKey.Trim() });
}

var platform = builder.AddMatPlatform<DockPlatform>(DockPlatform.Describe);

// ---------------------------------------------------------------------------
// Authentication / authorization
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(AuthConstants.CookieScheme)
    .AddCookie(AuthConstants.CookieScheme, options =>
    {
        options.Cookie.Name = "matdock.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromDays(Math.Max(1, sessionLifetimeDays));
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = SessionValidation.ValidateAsync;
    })
    // Sign-in through MatIdentity, only when MATIDENTITY_AUTHORITY is set (nothing changes without it).
    .AddMatIdentitySignIn<DockIdentityUsers>(platform, options =>
    {
        options.CookieScheme = AuthConstants.CookieScheme;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(nameof(UserRole.Admin)));
    // Backstop: any endpoint without an explicit policy (e.g. a future minimal-API map) still requires
    // an authenticated user. Razor Pages keep their AuthorizeFolder conventions; AllowAnonymous still wins.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ---------------------------------------------------------------------------
// Razor Pages (secure by default; open only the account pages)
// ---------------------------------------------------------------------------
builder.Services.AddRazorPages(options =>
{
    // Secure by default; open only the pages needed before/around login.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Setup");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Logout");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AuthorizeFolder("/Users", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Settings", "AdminOnly");
    // The role "User" looks at everything and starts/stops/restarts containers; everything that reaches the hosts beyond
    // that is for admins. Environments hold the SSH secrets and decide which hosts MatDock drives (a Local environment is
    // the Docker socket of MatDock's own host); the list stays visible.
    options.Conventions.AuthorizePage("/Environments/Edit", "AdminOnly");
    options.Conventions.AuthorizePage("/Environments/Migrate", "AdminOnly");
    // A volume with driver options can bind any host path (type=none, o=bind, device=/) → admins only.
    options.Conventions.AuthorizePage("/Volumes/Create", "AdminOnly");
    // Backups hold the data of every volume and the .env of the stacks; restoring overwrites volumes and stacks.
    options.Conventions.AuthorizeFolder("/Backups", "AdminOnly");
    // The volume file explorer can read/write arbitrary files inside volumes → admins only.
    options.Conventions.AuthorizePage("/Volumes/Files", "AdminOnly");
    options.Conventions.AuthorizePage("/Volumes/FileEdit", "AdminOnly");
    // Prune permanently deletes unused volumes/networks → admins only.
    options.Conventions.AuthorizePage("/Volumes/Prune", "AdminOnly");
    options.Conventions.AuthorizePage("/Networks/Prune", "AdminOnly");
    // Managed stacks deploy arbitrary compose (= arbitrary containers/binds) → admins only.
    options.Conventions.AuthorizeFolder("/Stacks", "AdminOnly");
    // App templates feed the stack editor (same power) → admins only.
    options.Conventions.AuthorizeFolder("/Apps", "AdminOnly");
    // Git credentials hold secrets and drive stack clones → admins only.
    options.Conventions.AuthorizeFolder("/GitCredentials", "AdminOnly");
    options.Conventions.AuthorizeFolder("/GitRepos", "AdminOnly");
    // Reverse-proxy route management + connection config → admins only.
    options.Conventions.AuthorizeFolder("/Proxy", "AdminOnly");
    // Registry browser (credentials + private image catalog) → admins only.
    options.Conventions.AuthorizeFolder("/Registry", "AdminOnly");
    // Scheduled tasks run prune/notification actions → admins only.
    options.Conventions.AuthorizeFolder("/Schedules", "AdminOnly");
    // The web terminal is an interactive shell to the host/containers → admins only.
    options.Conventions.AuthorizeFolder("/Terminal", "AdminOnly");
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// HTTP pipeline
// ---------------------------------------------------------------------------
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Ensure the web-app manifest is served with the correct MIME type (some hosts omit it).
var staticContentTypes = new FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticContentTypes,
    OnPrepareResponse = ctx =>
    {
        // The service worker and manifest must always be revalidated so the browser
        // detects a new version and updates the installed app on its own — never let a
        // proxy or the HTTP cache pin an old copy.
        var name = ctx.File.Name;
        if (name.Equals("sw.js", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("manifest.webmanifest", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});
app.UseWebSockets(new WebSocketOptions
{
    // Detect dead/half-open terminal clients: ping periodically and abort the socket if no pong
    // arrives within the timeout, so the pump loops end and the SSH session is released.
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    KeepAliveTimeout = TimeSpan.FromSeconds(30),
});
app.UseRouting();
// The rate limit of the anonymous platform endpoints /api and /api/health.
app.UseRateLimiter();
app.UseAuthentication();
// First-run: redirect everything to the setup wizard until an administrator exists.
app.UseMiddleware<SetupGuardMiddleware>();
app.UseMiddleware<PasswordChangeGuardMiddleware>();
app.UseAuthorization();
app.MapRazorPages();

// Interactive terminal (WebSocket ⇄ SSH PTY). Admin-only; the handler validates env + container id.
app.MapGet("/terminal/ws", MatDock.Web.Terminal.TerminalEndpoint.HandleAsync).RequireAuthorization("AdminOnly");

// Live container logs (one-directional WebSocket, docker logs -f). Any authenticated user, like the Logs page.
app.MapGet("/logs/ws", MatDock.Web.Logs.LogsEndpoint.HandleAsync).RequireAuthorization();

// Incoming Git webhook that triggers a sync job. Anonymous by design — the per-job URL token is the
// secret (AllowAnonymous is required to escape the fallback "authenticated user" policy).
app.MapPost("/webhooks/sync/{token}", MatDock.Web.Sync.WebhookSyncEndpoint.HandleAsync).AllowAnonymous();

// Machine-to-machine REST API (/api/v1, used by Mattiverse). Anonymous at the cookie level — the X-Api-Key header
// (MatDock__Api__Key) is the guard; without a configured key every call answers 503.
app.MapMatDockApi();

// The platform contract next to it: /api (definition) and /api/health anonymous and rate limited, /api/state, /api/configure and
// /api/actions with X-Api-Key (MAT_API_KEY, else MatDock__Api__Key) or a bearer token of MatIdentity. Never under /api/v1.
app.MapMatPlatform();

// ---------------------------------------------------------------------------
// Database migration + first-run seed
// ---------------------------------------------------------------------------
await DbBootstrapper.InitializeAsync(app.Services, app.Logger);

// One-time (idempotent) unification: mirror existing backup schedules and sync-job cron into the
// generic Schedules so a single runner drives their timing. Originals are kept (reversible).
using (var scope = app.Services.CreateScope())
{
    try
    {
        await scope.ServiceProvider.GetRequiredService<MatDock.Core.Schedules.ScheduleUnificationMigrator>().MigrateAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Schedule unification migration failed.");
    }
}

// Headless installs (Mattiverse): make sure the host's Docker socket is available as the "local" environment.
if (app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MatDockOptions>>().Value.Environments.EnsureLocal)
{
    using var scope = app.Services.CreateScope();
    try
    {
        var (ok, message, env, created) = await scope.ServiceProvider
            .GetRequiredService<MatDock.Core.Environments.EnvironmentService>().EnsureLocalAsync();
        if (ok)
        {
            app.Logger.LogInformation("EnsureLocal: {Message} ({Name}, id {Id}, created {Created}).", message, env?.Name, env?.Id, created);
        }
        else
        {
            app.Logger.LogWarning("EnsureLocal: {Message}", message);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "EnsureLocal failed.");
    }
}

app.Run();
