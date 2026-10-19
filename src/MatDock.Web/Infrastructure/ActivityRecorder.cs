using System.Reflection;
using MatDock.Core.Activity;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// Writes every action of a signed-in user (a POST of a page handler) into the activity log: the page and the handler,
/// what it was done to (a few named form fields, never anything else, so no secret gets in), the environment, and the
/// message the user saw. A failure of the log never fails the action.
/// </summary>
public sealed class ActivityPageFilter : IAsyncPageFilter
{
    /// <summary>The form fields that name the object of an action, in the order they are tried.</summary>
    private static readonly string[] TargetFields =
        ["name", "Input.Name", "project", "stack", "volume", "network", "reference", "container", "target", "fileName", "id", "Input.Id"];

    private static readonly string[] EnvironmentFields = ["envId", "EnvId", "env", "Input.EnvironmentId", "environmentId"];

    /// <summary>Pages whose POSTs are not actions on the system (sign-in, sign-out, first setup).</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "/Account/Login", "/Account/Logout", "/Setup" };

    private readonly ILogger<ActivityPageFilter> _logger;

    public ActivityPageFilter(ILogger<ActivityPageFilter> logger) => _logger = logger;

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var executed = await next();
        var http = context.HttpContext;
        var page = context.ActionDescriptor.ViewEnginePath;
        if (!HttpMethods.IsPost(http.Request.Method) || http.User.Identity?.IsAuthenticated != true || Skipped.Contains(page))
        {
            return;
        }

        try
        {
            var form = http.Request.HasFormContentType ? http.Request.Form : null;
            string? Field(string key) => form is not null && form.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;

            var handler = context.HandlerMethod?.Name;
            var verb = Field("action") ?? handler;
            var action = Describe(page) + (string.IsNullOrEmpty(verb) ? string.Empty : " · " + verb);
            var target = TargetFields.Select(Field).FirstOrDefault(v => v is not null);

            string? environment = null;
            if (EnvironmentFields.Select(Field).FirstOrDefault(v => v is not null) is { } envText && long.TryParse(envText, out var envId) && envId > 0)
            {
                environment = (await http.RequestServices.GetRequiredService<EnvironmentService>().GetAsync(envId, http.RequestAborted))?.Name;
            }

            var (ok, message) = Outcome(context, executed);
            await http.RequestServices.GetRequiredService<ActivityLogService>().RecordAsync(
                http.User.Identity?.Name ?? "?", "ui", action, target, environment, ok, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Writing the activity log failed for {Page}.", page);
        }
    }

    /// <summary>"/Containers/Index" → "Containers", "/Stacks/SyncJobs/Edit" → "Stacks / SyncJobs / Edit".</summary>
    public static string Describe(string page)
    {
        var parts = page.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1 && parts[^1] == "Index")
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts.Count == 0 ? "Dashboard" : string.Join(" / ", parts);
    }

    private static (bool Ok, string? Message) Outcome(PageHandlerExecutingContext context, PageHandlerExecutedContext executed)
    {
        if (executed.Exception is { } ex && !executed.ExceptionHandled)
        {
            return (false, ex.Message);
        }

        // The pages tell the user how it went in StatusMessage / IsError (TempData), or in the errors of the form.
        var model = context.HandlerInstance;
        var message = Property<string>(model, "StatusMessage");
        var isError = Property<bool?>(model, "IsError") ?? false;
        if (!context.ModelState.IsValid)
        {
            var error = context.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
            return (false, error ?? message);
        }

        if (executed.Result is Microsoft.AspNetCore.Mvc.ForbidResult or Microsoft.AspNetCore.Mvc.NotFoundResult)
        {
            return (false, executed.Result is Microsoft.AspNetCore.Mvc.ForbidResult ? "Not allowed." : "Not found.");
        }

        return (!isError, message);
    }

    private static T? Property<T>(object? model, string name)
    {
        var property = model?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(model) is T value ? value : default;
    }
}

/// <summary>Writes the changing calls of the APIs (/api/v1 and the platform's /api/configure and /api/actions) into the activity log.</summary>
public static class ActivityApiRecorder
{
    public static IApplicationBuilder UseApiActivity(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var path = http.Request.Path.Value ?? string.Empty;
        var recorded = !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method)
                       && (path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith("/api/actions/", StringComparison.OrdinalIgnoreCase)
                           || path.Equals("/api/configure", StringComparison.OrdinalIgnoreCase));
        await next();
        if (!recorded || http.Response.StatusCode is 401 or 403 or 503)
        {
            return; // nothing happened for a caller without a valid key
        }

        try
        {
            var actor = http.User.Identity?.IsAuthenticated == true ? http.User.Identity.Name ?? "API" : "API";
            await http.RequestServices.GetRequiredService<ActivityLogService>().RecordAsync(
                actor, "api", $"{http.Request.Method} {path}", null, http.Request.Query["environment"].ToString(),
                http.Response.StatusCode < 400, $"HTTP {http.Response.StatusCode}", CancellationToken.None);
        }
        catch (Exception ex)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Activity").LogWarning(ex, "Writing the activity log failed for {Path}.", path);
        }
    });
}
