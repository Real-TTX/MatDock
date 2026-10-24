using MatDock.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MatDock.Web.Infrastructure;

/// <summary>
/// A viewer changes nothing: every page handler that is not a GET answers "access denied" to one, whatever the page shows. Only the own account
/// (sign-out, profile and password) stays open. The pages hide their buttons too; this is the guard behind them.
/// </summary>
public sealed class ViewerReadOnlyFilter : IAsyncPageFilter
{
    private static readonly HashSet<string> Open = new(StringComparer.OrdinalIgnoreCase) { "/Account/Logout", "/Account/Profile", "/Account/Login" };

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
            && http.User.IsInRole(nameof(UserRole.Viewer))
            && !Open.Contains(context.ActionDescriptor.ViewEnginePath))
        {
            context.Result = new ForbidResult();
            return Task.CompletedTask;
        }

        return next();
    }
}
