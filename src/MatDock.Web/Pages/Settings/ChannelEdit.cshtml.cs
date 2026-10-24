using MatDock.Core.Entities;
using MatDock.Core.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings;

/// <summary>One channel of the notifications (Discord, Slack, Teams, Telegram, ntfy, Gotify): its address or token, a test, delete.</summary>
public class ChannelEditModel : PageModel
{
    private readonly NotificationChannelService _channels;

    public ChannelEditModel(NotificationChannelService channels) => _channels = channels;

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public NotificationChannelInput Input { get; set; } = new();

    public bool HasSecret { get; private set; }
    public bool IsEdit => Id is > 0;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (IsEdit)
        {
            var channel = await _channels.GetAsync(Id!.Value, HttpContext.RequestAborted);
            if (channel is null)
            {
                return RedirectToPage("/Settings/Notifications");
            }

            Input = new NotificationChannelInput { Name = channel.Name, Type = channel.Type, Url = channel.Url, Target = channel.Target, Enabled = channel.Enabled };
            HasSecret = channel.EncryptedSecret is not null;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var (ok, message, id) = await _channels.SaveAsync(Id, Input, HttpContext.RequestAborted);
        if (!ok)
        {
            ModelState.AddModelError(string.Empty, message);
            HasSecret = IsEdit && (await _channels.GetAsync(Id!.Value, HttpContext.RequestAborted))?.EncryptedSecret is not null;
            Input.Secret = null;
            return Page();
        }

        StatusMessage = message;
        return RedirectToPage("/Settings/Notifications");
    }

    /// <summary>Sends a test message through the channel (from this page or the list).</summary>
    public async Task<IActionResult> OnPostTestAsync(long id)
    {
        var (ok, message) = await _channels.TestAsync(id, HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage("/Settings/Notifications");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        StatusMessage = await _channels.DeleteAsync(id, HttpContext.RequestAborted) ? "Channel deleted." : "Channel not found.";
        return RedirectToPage("/Settings/Notifications");
    }
}
