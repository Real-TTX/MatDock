using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Core.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Environments;

/// <summary>One environment: what its Docker engine says about itself (version, host, storage, runtimes, warnings), its disk usage, and a cleanup.</summary>
public class DetailModel : PageModel
{
    private readonly EnvironmentService _environments;
    private readonly DockerSystemService _system;
    private readonly OperationRegistry _operations;

    public DetailModel(EnvironmentService environments, DockerSystemService system, OperationRegistry operations)
    {
        _environments = environments;
        _system = system;
        _operations = operations;
    }

    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Op { get; set; }

    public DockerEnvironment? Environment { get; private set; }
    public DockerSystemOverview? Overview { get; private set; }
    public string? Error { get; private set; }
    public Operation? Running { get; private set; }

    public bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

    public async Task<IActionResult> OnGetAsync()
    {
        Environment = await _environments.GetAsync(Id, HttpContext.RequestAborted);
        if (Environment is null)
        {
            return NotFound();
        }

        Running = string.IsNullOrEmpty(Op) ? null : _operations.Get(Op);
        if (!Environment.IsEnabled)
        {
            Error = "This environment is disabled.";
            return Page();
        }

        try
        {
            Overview = await _system.GetOverviewAsync(Environment, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }

        return Page();
    }

    /// <summary>Removes what the form names on this environment, in the background (admins only).</summary>
    public async Task<IActionResult> OnPostCleanupAsync(bool containers, bool danglingImages, bool allImages, bool networks, bool buildCache)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var env = await _environments.GetAsync(Id, HttpContext.RequestAborted);
        if (env is null || !env.IsEnabled)
        {
            return NotFound();
        }

        var cleanup = new SystemCleanup(containers, danglingImages, allImages, networks, buildCache);
        var envId = env.Id;
        var operation = _operations.Start("system-cleanup", env.Name, $"cleanup:{envId}", async ctx =>
        {
            var target = await ctx.Services.GetRequiredService<EnvironmentService>().GetAsync(envId, ctx.CancellationToken);
            if (target is null)
            {
                return OperationOutcome.Failure("Environment not found.");
            }

            var (ok, lines) = await ctx.Services.GetRequiredService<DockerSystemService>().CleanupAsync(target, cleanup, ctx.CancellationToken);
            foreach (var line in lines)
            {
                ctx.Log(line);
            }

            return ok ? OperationOutcome.Success() : OperationOutcome.Failure("A step failed; see above.");
        });
        return RedirectToPage(new { Id, op = operation.Id });
    }
}
