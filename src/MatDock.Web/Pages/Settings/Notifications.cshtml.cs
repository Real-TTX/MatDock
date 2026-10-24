using MatDock.Core.Entities;
using MatDock.Core.Notifications;
using MatDock.Core.Schedules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDock.Web.Pages.Settings;

public class NotificationsModel : PageModel
{
    private readonly NotificationSettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private readonly NotificationChannelService _channels;
    private readonly ScheduleService _schedules;

    public NotificationsModel(NotificationSettingsService settingsService, INotificationService notificationService, NotificationChannelService channels, ScheduleService schedules)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _channels = channels;
        _schedules = schedules;
    }

    /// <summary>The events of the alerts a notification can be switched on for here (a schedule "Send a notification" each).</summary>
    public static readonly IReadOnlyList<(ScheduleEvent Event, string Label)> AlertEvents =
    [
        (ScheduleEvent.ContainerDied, "A container died with an error"),
        (ScheduleEvent.ContainerUnhealthy, "A container turned unhealthy"),
        (ScheduleEvent.ContainerRestartLoop, "A container is in a restart loop"),
        (ScheduleEvent.HostThreshold, "A host is over a limit"),
        (ScheduleEvent.EnvironmentOffline, "An environment went offline"),
    ];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public AlertInput Alerts { get; set; } = new();

    public IReadOnlyList<NotificationChannel> Channels { get; private set; } = Array.Empty<NotificationChannel>();

    public sealed class AlertInput
    {
        public int? Cpu { get; set; }
        public int? Mem { get; set; }
        public int? Disk { get; set; }
        public int RestartCount { get; set; } = 3;
        public int RestartMinutes { get; set; } = 10;
        public List<ScheduleEvent> Notify { get; set; } = [];
    }

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
        await LoadExtrasAsync(s);
    }

    /// <summary>The channels and the alerts (their limits and which events notify).</summary>
    private async Task LoadExtrasAsync(NotificationSettings s)
    {
        var ct = HttpContext.RequestAborted;
        Channels = await _channels.GetAllAsync(ct);
        var notify = new List<ScheduleEvent>();
        foreach (var (evt, _) in AlertEvents)
        {
            if (await _schedules.NotifiesOnAsync(evt, ct))
            {
                notify.Add(evt);
            }
        }

        Alerts = new AlertInput
        {
            Cpu = s.HostCpuLimit, Mem = s.HostMemLimit, Disk = s.HostDiskLimit,
            RestartCount = s.RestartLoopCount, RestartMinutes = s.RestartLoopMinutes, Notify = notify,
        };
    }

    /// <summary>Saves the limits of the alerts and switches the notification of each alert event on or off.</summary>
    public async Task<IActionResult> OnPostAlertsAsync()
    {
        var ct = HttpContext.RequestAborted;
        var problem = await _settingsService.UpdateAlertsAsync(Alerts.Cpu, Alerts.Mem, Alerts.Disk, Alerts.RestartCount, Alerts.RestartMinutes, ct);
        if (problem is null)
        {
            foreach (var (evt, _) in AlertEvents)
            {
                await _schedules.SetNotifyOnAsync(evt, Alerts.Notify.Contains(evt), ct);
            }
        }

        StatusMessage = problem ?? "Alerts saved.";
        IsError = problem is not null;
        return RedirectToPage();
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
            await LoadExtrasAsync(current);
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
