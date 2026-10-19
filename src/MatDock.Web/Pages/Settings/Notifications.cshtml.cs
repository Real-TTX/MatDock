using MatDock.Core.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings;

public class NotificationsModel : PageModel
{
    private readonly NotificationSettingsService _settingsService;
    private readonly INotificationService _notificationService;

    public NotificationsModel(NotificationSettingsService settingsService, INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool HasStoredPassword { get; private set; }

    /// <summary>MatSMTP, when Mattiverse set it: the server fields are then fixed (shown, not saved).</summary>
    public MailRelay? Relay => _settingsService.Relay;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public class InputModel
    {
        public bool NotifyOnSuccess { get; set; }
        public bool NotifyOnFailure { get; set; } = true;

        public bool SmtpEnabled { get; set; }
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        public bool SmtpUseTls { get; set; } = true;
        public string? SmtpUsername { get; set; }
        public string? SmtpPassword { get; set; }
        public string? SmtpFrom { get; set; }
        public string? SmtpTo { get; set; }

        public bool WebhookEnabled { get; set; }
        public string? WebhookUrl { get; set; }
    }

    public async Task OnGetAsync()
    {
        var s = await _settingsService.GetAsync(HttpContext.RequestAborted);
        HasStoredPassword = !string.IsNullOrEmpty(s.EncryptedSmtpPassword);
        Input = new InputModel
        {
            NotifyOnSuccess = s.NotifyOnSuccess,
            NotifyOnFailure = s.NotifyOnFailure,
            SmtpEnabled = s.SmtpEnabled,
            SmtpHost = s.SmtpHost,
            SmtpPort = s.SmtpPort,
            SmtpUseTls = s.SmtpUseTls,
            SmtpUsername = s.SmtpUsername,
            SmtpFrom = s.SmtpFrom,
            SmtpTo = s.SmtpTo,
            WebhookEnabled = s.WebhookEnabled,
            WebhookUrl = s.WebhookUrl
        };
        ShowRelay();
    }

    /// <summary>With MatSMTP the server fields show its values (the password never); the stored own ones stay in the database.</summary>
    private void ShowRelay()
    {
        if (Relay is { } relay)
        {
            Input.SmtpHost = relay.Host;
            Input.SmtpPort = relay.Port;
            Input.SmtpUseTls = relay.Security != MailSecurity.None;
            Input.SmtpUsername = relay.Username;
            Input.SmtpPassword = null;
            Input.SmtpFrom = relay.From;
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (Relay is not null)
        {
            // The server fields are disabled (not posted) and never saved; only the recipients are checked.
            foreach (var key in new[] { "Input.SmtpHost", "Input.SmtpPort", "Input.SmtpUseTls", "Input.SmtpUsername", "Input.SmtpPassword", "Input.SmtpFrom" })
            {
                ModelState.Remove(key);
            }

            if (Input.SmtpEnabled && string.IsNullOrWhiteSpace(Input.SmtpTo))
            {
                ModelState.AddModelError("Input.SmtpTo", "Email requires recipients.");
            }
        }
        else if (Input.SmtpEnabled && (string.IsNullOrWhiteSpace(Input.SmtpHost) || string.IsNullOrWhiteSpace(Input.SmtpFrom) || string.IsNullOrWhiteSpace(Input.SmtpTo)))
        {
            ModelState.AddModelError(string.Empty, "Email requires a host, sender and recipients.");
        }

        if (Input.WebhookEnabled && (string.IsNullOrWhiteSpace(Input.WebhookUrl) || !Uri.TryCreate(Input.WebhookUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)))
        {
            ModelState.AddModelError("Input.WebhookUrl", "Please enter a valid http(s) URL.");
        }

        if (!ModelState.IsValid)
        {
            var current = await _settingsService.GetAsync(HttpContext.RequestAborted);
            HasStoredPassword = !string.IsNullOrEmpty(current.EncryptedSmtpPassword);
            ShowRelay();
            return Page();
        }

        await _settingsService.UpdateAsync(new NotificationSettingsInput
        {
            NotifyOnSuccess = Input.NotifyOnSuccess,
            NotifyOnFailure = Input.NotifyOnFailure,
            SmtpEnabled = Input.SmtpEnabled,
            SmtpHost = Input.SmtpHost,
            SmtpPort = Input.SmtpPort,
            SmtpUseTls = Input.SmtpUseTls,
            SmtpUsername = Input.SmtpUsername,
            SmtpPassword = Input.SmtpPassword,
            SmtpFrom = Input.SmtpFrom,
            SmtpTo = Input.SmtpTo,
            WebhookEnabled = Input.WebhookEnabled,
            WebhookUrl = Input.WebhookUrl
        }, HttpContext.RequestAborted);

        StatusMessage = "Notifications saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        var (ok, message) = await _notificationService.SendTestAsync(HttpContext.RequestAborted);
        StatusMessage = message;
        IsError = !ok;
        return RedirectToPage();
    }
}
