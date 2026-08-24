using MatDock.Core.Entities;
using MatDock.Core.Environments;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages;

public class IndexModel : PageModel
{
    private readonly EnvironmentService _environmentService;

    public IndexModel(EnvironmentService environmentService)
    {
        _environmentService = environmentService;
    }

    public int TotalEnvironments { get; private set; }
    public int OnlineCount { get; private set; }
    public int AttentionCount { get; private set; }
    public IReadOnlyList<DockerEnvironment> Recent { get; private set; } = new List<DockerEnvironment>();

    public async Task OnGetAsync()
    {
        var all = await _environmentService.GetAllAsync(HttpContext.RequestAborted);
        TotalEnvironments = all.Count;
        OnlineCount = all.Count(e => e.Status == EnvironmentStatus.Online);
        AttentionCount = all.Count(e => e.Status is EnvironmentStatus.Offline or EnvironmentStatus.Error);
        Recent = all
            .OrderByDescending(e => e.LastCheckedAt ?? e.UpdateDate)
            .Take(5)
            .ToList();
    }
}
