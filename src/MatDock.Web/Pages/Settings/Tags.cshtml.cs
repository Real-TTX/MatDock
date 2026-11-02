using MatDock.Core.Entities;
using MatDock.Core.Stacks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings;

/// <summary>The tag catalog: the names and colours the lists of stacks show and filter by. Renaming a tag renames it on every stack; deleting drops it.</summary>
public class TagsModel : PageModel
{
    private readonly TagService _tags;

    public TagsModel(TagService tags) => _tags = tags;

    public List<Tag> Tags { get; private set; } = [];
    public Dictionary<string, int> Usage { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task OnGetAsync()
    {
        Tags = await _tags.GetAllAsync(HttpContext.RequestAborted);
        Usage = await _tags.UsageAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostSaveAsync(string name, string? color)
    {
        var (ok, message) = await _tags.SaveAsync(name ?? string.Empty, color, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRenameAsync(long id, string name)
    {
        var (ok, message) = await _tags.RenameAsync(id, name ?? string.Empty, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var deleted = await _tags.DeleteAsync(id, HttpContext.RequestAborted);
        StatusMessage = deleted ? "Tag deleted." : "Tag not found.";
        IsError = !deleted;
        return RedirectToPage();
    }
}
