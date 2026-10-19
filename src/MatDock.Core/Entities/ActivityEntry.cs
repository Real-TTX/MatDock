namespace MatDock.Core.Entities;

/// <summary>
/// One thing someone did in MatDock (a button on a page, a call of the API): who, where, on what, and how it ended.
/// Written by the web layer after the action; old entries are removed after <see cref="Activity.ActivityLogService.KeepDays"/>.
/// </summary>
public class ActivityEntry : AuditableEntity
{
    /// <summary>The user name, or "API" / the caller of the API.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>"ui" or "api".</summary>
    public string Source { get; set; } = "ui";

    /// <summary>What was done, e.g. "Containers · Stop" or "PUT /api/v1/stacks/shop".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>What it was done to, e.g. "web-1"; may be empty.</summary>
    public string? Target { get; set; }

    /// <summary>The environment, when the action names one.</summary>
    public string? Environment { get; set; }

    public bool Ok { get; set; } = true;

    /// <summary>The message the user saw (or the status code of the API), shortened.</summary>
    public string? Message { get; set; }
}
