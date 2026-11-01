using MatDock.Core.Entities;
using MatDock.Core.Stacks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Stacks;

/// <summary>The deploy history of a managed stack: every deploy with its trigger, options, duration and outcome, one log at a time.</summary>
public class DeploysModel : PageModel
{
    private readonly StackService _stacks;
    private readonly DeploymentHistory _history;

    public DeploysModel(StackService stacks, DeploymentHistory history)
    {
        _stacks = stacks;
        _history = history;
    }

    public Stack? Stack { get; private set; }

    public List<StackDeployment> Deploys { get; private set; } = [];

    /// <summary>The deploy whose log is shown (<c>?show=</c>).</summary>
    public StackDeployment? Shown { get; private set; }

    public int Succeeded => Deploys.Count(d => d.Ok == true);

    public int Failed => Deploys.Count(d => d.Ok == false);

    public async Task<IActionResult> OnGetAsync(long id, long? show)
    {
        Stack = await _stacks.GetAsync(id, HttpContext.RequestAborted);
        if (Stack is null)
        {
            return RedirectToPage("Index");
        }

        Deploys = await _history.GetAsync(id, DeploymentHistory.Keep, HttpContext.RequestAborted);
        Shown = show is > 0 ? Deploys.FirstOrDefault(d => d.Id == show) : null;
        return Page();
    }

    /// <summary>"1 min 12 s", "8 s", or a dash while it runs.</summary>
    public static string Duration(StackDeployment d)
    {
        if (d.FinishedAt is not { } end)
        {
            return "–";
        }

        var span = end - d.StartedAt;
        return span.TotalSeconds < 60 ? $"{Math.Max(0, (int)Math.Round(span.TotalSeconds))} s" : $"{(int)span.TotalMinutes} min {span.Seconds} s";
    }
}
