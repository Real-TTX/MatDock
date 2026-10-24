using MatDock.Core.Docker;
using MatDock.Core.Entities;
using MatDock.Core.Environments;
using MatDock.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Events;

/// <summary>
/// What happened on the engines (<c>docker events</c>) in a window of time: containers that started, died or turned unhealthy, images pulled, volumes and
/// networks made and removed. Of the environment chosen in the menu, or of all; filtered by type, by the problems only, by a container, or by text.
/// </summary>
public class IndexModel : PageModel
{
    private readonly EnvironmentService _environments;
    private readonly DockerSystemService _system;

    public IndexModel(EnvironmentService environments, DockerSystemService system)
    {
        _environments = environments;
        _system = system;
    }

    [BindProperty(SupportsGet = true)] public int Hours { get; set; } = 24;
    [BindProperty(SupportsGet = true)] public string? Type { get; set; }
    /// <summary>What to show: empty = without the probes of health checks (<c>exec_*</c>, the bulk of a busy engine), <c>problems</c>, or <c>all</c>.</summary>
    [BindProperty(SupportsGet = true)] public string? Show { get; set; }
    [BindProperty(SupportsGet = true)] public string? Container { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public sealed record Row(DockerEnvironment Env, DockerEvent Event);

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();
    public bool IsAll { get; private set; }

    /// <summary>An engine answered as many events as Docker keeps: older ones of the window are gone.</summary>
    public bool Capped { get; private set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Hours = DockerEvents.Windows.Contains(Hours) ? Hours : 24;
        Type = Type is not null && DockerEvents.Types.Contains(Type) ? Type : null;

        var environments = await _environments.GetEnabledAsync(ct);
        var selected = EnvSelection.Resolve(HttpContext);
        if (selected is { } id && environments.All(e => e.Id != id))
        {
            selected = null;
            EnvSelection.Clear(HttpContext);
        }

        IsAll = selected is null;
        var targets = IsAll ? environments : environments.Where(e => e.Id == selected).ToList();

        // A few hosts at a time, like the container list.
        using var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(targets.Select(async env =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return (env, result: await _system.GetEventsAsync(env, Hours, Type, null, Container, ct));
            }
            finally
            {
                gate.Release();
            }
        }));

        Errors = results.Where(r => r.result.Error is not null).Select(r => $"{r.env.Name}: {r.result.Error}").ToList();
        Capped = results.Any(r => r.result.Events.Count >= DockerEvents.DaemonKeeps);
        Show = Show is "problems" or "all" ? Show : null;
        Rows = results
            .SelectMany(r => DockerEvents.Matching(r.result.Events, Q)
                .Where(e => Show switch { "problems" => e.IsProblem, "all" => true, _ => !e.IsProbe })
                .Select(e => new Row(r.env, e)))
            .OrderByDescending(r => r.Event.TimeUtc)
            .ToList();
    }

    public string LinkOf(Row row) => row.Event switch
    {
        { Type: "container", Action: not ("destroy" or "remove") } e => $"/Containers/ContainerDetail?envId={row.Env.Id}&id={Uri.EscapeDataString(e.Id)}",
        { Type: "image", Action: not ("delete" or "untag") } e when e.Name is not null => $"/Images/Detail?envId={row.Env.Id}&reference={Uri.EscapeDataString(e.Name)}",
        { Type: "volume", Action: not "destroy" } e when e.Name is not null => $"/Volumes/Detail?envId={row.Env.Id}&volume={Uri.EscapeDataString(e.Name)}",
        { Type: "network", Action: not "destroy" } e => $"/Networks/Detail?envId={row.Env.Id}&network={Uri.EscapeDataString(e.Name ?? e.Id)}",
        _ => string.Empty,
    };
}
