using MatDock.Core;
using MatDock.Core.Configuration;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
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

// Honour X-Forwarded-* from a TLS-terminating reverse proxy so Request.IsHttps is correct and the
// auth cookie is marked Secure when the client connection is HTTPS.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddMatDockCore();
builder.Services.AddHostedService<BackupSchedulerService>();

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
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Logout");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AuthorizeFolder("/Users", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Settings", "AdminOnly");
    // The volume file explorer can read/write arbitrary files inside volumes → admins only.
    options.Conventions.AuthorizePage("/Volumes/Files", "AdminOnly");
    options.Conventions.AuthorizePage("/Volumes/FileEdit", "AdminOnly");
    // Managed stacks deploy arbitrary compose (= arbitrary containers/binds) → admins only.
    options.Conventions.AuthorizeFolder("/Stacks", "AdminOnly");
    // App templates feed the stack editor (same power) → admins only.
    options.Conventions.AuthorizeFolder("/Apps", "AdminOnly");
    // Git credentials hold secrets and drive stack clones → admins only.
    options.Conventions.AuthorizeFolder("/GitCredentials", "AdminOnly");
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

app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions
{
    // Detect dead/half-open terminal clients: ping periodically and abort the socket if no pong
    // arrives within the timeout, so the pump loops end and the SSH session is released.
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    KeepAliveTimeout = TimeSpan.FromSeconds(30),
});
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<PasswordChangeGuardMiddleware>();
app.UseAuthorization();
app.MapRazorPages();

// Interactive terminal (WebSocket ⇄ SSH PTY). Admin-only; the handler validates env + container id.
app.MapGet("/terminal/ws", MatDock.Web.Terminal.TerminalEndpoint.HandleAsync).RequireAuthorization("AdminOnly");

// Live container logs (one-directional WebSocket, docker logs -f). Any authenticated user, like the Logs page.
app.MapGet("/logs/ws", MatDock.Web.Logs.LogsEndpoint.HandleAsync).RequireAuthorization();

// ---------------------------------------------------------------------------
// Database migration + first-run seed
// ---------------------------------------------------------------------------
await DbBootstrapper.InitializeAsync(app.Services, app.Logger);

app.Run();
