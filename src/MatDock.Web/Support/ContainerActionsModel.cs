using MatDock.Core.Containers;

namespace MatDock.Web.Support;

/// <summary>
/// The actions of one container in a list (<c>_ContainerActions</c>): the power button in its colour (green Start, red Stop), the logs, and the rest
/// behind one "more" button (MatUi menu, LIST-09). The forms post to <see cref="Handler"/> of the page they are on.
/// </summary>
/// <param name="Handler">The page handler of the container actions (envId, id, name, action).</param>
/// <param name="Itself">The container is a MatDock that must not be stopped from here (<see cref="MatDock.Core.Execution.SelfContainer.IsOn"/>).</param>
public sealed record ContainerActionsModel(long EnvId, DockerContainer Container, string Handler, bool CanControl, bool IsAdmin, bool Itself)
{
    /// <summary>The compose project, for a page that needs it on its forms (the containers of a stack).</summary>
    public string? Project { get; init; }

    /// <summary>The handler that removes a stopped container (only where the page has one).</summary>
    public string? RemoveHandler { get; init; }

    /// <summary>Offer pause and kill too (the containers page).</summary>
    public bool Extended { get; init; }

    /// <summary>The address the container's "Open" goes to.</summary>
    public string? OpenUrl { get; init; }

    public bool Paused => string.Equals(Container.State, "paused", StringComparison.OrdinalIgnoreCase);

    /// <summary>The id of its menu: unique on a page that lists the containers of all environments.</summary>
    public string MenuId => $"cm-{EnvId}-{new string(Container.Id.Where(char.IsAsciiLetterOrDigit).ToArray())}";
}
