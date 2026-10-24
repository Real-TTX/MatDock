using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using MatDock.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Notifications;

/// <summary>The form of a channel; an empty <see cref="Secret"/> keeps the stored one.</summary>
public sealed class NotificationChannelInput
{
    public string Name { get; set; } = string.Empty;
    public NotificationChannelType Type { get; set; }
    public string? Url { get; set; }
    public string? Target { get; set; }
    public string? Secret { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>The request each kind of channel wants: the message of Discord, Slack, Teams, Telegram, ntfy and Gotify.</summary>
public static partial class ChannelMessages
{
    public const string NtfyDefault = "https://ntfy.sh";

    [GeneratedRegex("^-?[0-9]{1,20}$|^@[A-Za-z0-9_]{5,32}$")]
    private static partial Regex TelegramChat();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NtfyTopic();

    /// <summary>What is wrong with a channel (null = fine); <paramref name="hasSecret"/> says whether one is stored already.</summary>
    public static string? Problem(NotificationChannelInput input, bool hasSecret)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Please enter a name.";
        }

        var secret = input.Secret?.Trim();
        var needsSecret = input.Type is not NotificationChannelType.Ntfy;
        if (needsSecret && string.IsNullOrEmpty(secret) && !hasSecret)
        {
            return input.Type switch
            {
                NotificationChannelType.Telegram => "Please enter the token of the bot.",
                NotificationChannelType.Gotify => "Please enter the token of the application.",
                _ => "Please enter the address of the webhook.",
            };
        }

        if (input.Type is NotificationChannelType.Discord or NotificationChannelType.Slack or NotificationChannelType.Teams
            && !string.IsNullOrEmpty(secret) && !IsHttps(secret))
        {
            return "The address of the webhook starts with https://.";
        }

        return input.Type switch
        {
            NotificationChannelType.Telegram when !TelegramChat().IsMatch(input.Target?.Trim() ?? string.Empty)
                => "Please enter the chat id (a number, or @channel).",
            NotificationChannelType.Ntfy when !NtfyTopic().IsMatch(input.Target?.Trim() ?? string.Empty)
                => "Please enter the topic (letters, digits, - and _).",
            NotificationChannelType.Ntfy when !string.IsNullOrWhiteSpace(input.Url) && !IsHttp(input.Url.Trim())
                => "The server is an http(s) address.",
            NotificationChannelType.Gotify when string.IsNullOrWhiteSpace(input.Url) || !IsHttp(input.Url.Trim())
                => "Please enter the address of the Gotify server.",
            _ => null,
        };
    }

    /// <summary>The request that delivers <paramref name="subject"/> and <paramref name="body"/> through <paramref name="channel"/>.</summary>
    public static HttpRequestMessage Build(NotificationChannel channel, string? secret, string subject, string body)
    {
        HttpRequestMessage Json(string url, object payload) => new(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        switch (channel.Type)
        {
            case NotificationChannelType.Discord:
                return Json(secret!, new { content = Cut($"**{subject}**\n{body}", 2000) });
            case NotificationChannelType.Slack:
                return Json(secret!, new { text = Cut($"*{subject}*\n{body}", 3000) });
            case NotificationChannelType.Teams:
                return Json(secret!, new
                {
                    type = "message",
                    attachments = new[]
                    {
                        new
                        {
                            contentType = "application/vnd.microsoft.card.adaptive",
                            content = new Dictionary<string, object>
                            {
                                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                                ["type"] = "AdaptiveCard",
                                ["version"] = "1.4",
                                ["body"] = new object[]
                                {
                                    new { type = "TextBlock", text = subject, weight = "Bolder", wrap = true },
                                    new { type = "TextBlock", text = Cut(body, 8000), wrap = true },
                                },
                            },
                        },
                    },
                });
            case NotificationChannelType.Telegram:
                return Json($"https://api.telegram.org/bot{secret}/sendMessage", new { chat_id = channel.Target, text = Cut($"{subject}\n\n{body}", 4096), disable_web_page_preview = true });
            case NotificationChannelType.Ntfy:
            {
                var request = Json(Server(channel.Url, NtfyDefault), new { topic = channel.Target, title = subject, message = Cut(body, 4000) });
                if (!string.IsNullOrEmpty(secret))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                }

                return request;
            }

            case NotificationChannelType.Gotify:
            {
                var request = Json(Server(channel.Url, string.Empty) + "/message", new { title = subject, message = Cut(body, 8000), priority = 5 });
                request.Headers.Add("X-Gotify-Key", secret);
                return request;
            }

            default:
                throw new InvalidOperationException("Unknown channel.");
        }
    }

    private static string Server(string? url, string fallback) => (string.IsNullOrWhiteSpace(url) ? fallback : url.Trim()).TrimEnd('/');

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static bool IsHttp(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}

/// <summary>The channels of the notifications: kept with their secret encrypted, tested one by one, and every notification sent to each enabled one.</summary>
public sealed class NotificationChannelService
{
    private readonly MatDockDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NotificationChannelService> _logger;

    public NotificationChannelService(MatDockDbContext db, ISecretProtector secrets, IHttpClientFactory http, ILogger<NotificationChannelService> logger)
    {
        _db = db;
        _secrets = secrets;
        _http = http;
        _logger = logger;
    }

    public Task<List<NotificationChannel>> GetAllAsync(CancellationToken ct = default)
        => _db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    public Task<NotificationChannel?> GetAsync(long id, CancellationToken ct = default)
        => _db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<bool> AnyEnabledAsync(CancellationToken ct = default) => _db.NotificationChannels.AnyAsync(c => c.Enabled, ct);

    /// <summary>Creates (<paramref name="id"/> null) or changes a channel.</summary>
    public async Task<(bool Ok, string Message, long Id)> SaveAsync(long? id, NotificationChannelInput input, CancellationToken ct = default)
    {
        var channel = id is > 0 ? await GetAsync(id.Value, ct) : new NotificationChannel();
        if (channel is null)
        {
            return (false, "Channel not found.", 0);
        }

        if (ChannelMessages.Problem(input, channel.EncryptedSecret is not null && channel.Type == input.Type) is { } problem)
        {
            return (false, problem, channel.Id);
        }

        if (channel.Type != input.Type && string.IsNullOrWhiteSpace(input.Secret))
        {
            channel.EncryptedSecret = null; // a secret of another kind of channel means nothing here
        }

        channel.Name = input.Name.Trim();
        channel.Type = input.Type;
        channel.Url = string.IsNullOrWhiteSpace(input.Url) ? null : input.Url.Trim();
        channel.Target = string.IsNullOrWhiteSpace(input.Target) ? null : input.Target.Trim();
        channel.Enabled = input.Enabled;
        if (!string.IsNullOrWhiteSpace(input.Secret))
        {
            channel.EncryptedSecret = _secrets.Protect(input.Secret.Trim());
        }

        if (channel.Id == 0)
        {
            _db.NotificationChannels.Add(channel);
        }

        await _db.SaveChangesAsync(ct);
        return (true, "Channel saved.", channel.Id);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var channel = await GetAsync(id, ct);
        if (channel is null)
        {
            return false;
        }

        _db.NotificationChannels.Remove(channel);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Sends a test message through one channel (enabled or not).</summary>
    public async Task<(bool Ok, string Message)> TestAsync(long id, CancellationToken ct = default)
    {
        var channel = await GetAsync(id, ct);
        if (channel is null)
        {
            return (false, "Channel not found.");
        }

        var error = await SendAsync(channel, "MatDock test notification", "This is a test notification from MatDock.", ct);
        return error is null ? (true, $"Test message sent to {channel.Name}.") : (false, $"{channel.Name}: {error}");
    }

    /// <summary>Sends to every enabled channel; answers the errors ("name: why"), empty when all went out.</summary>
    public async Task<List<string>> SendAllAsync(string subject, string body, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var channel in await _db.NotificationChannels.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct))
        {
            if (await SendAsync(channel, subject, body, ct) is { } error)
            {
                errors.Add($"{channel.Name}: {error}");
            }
        }

        return errors;
    }

    private async Task<string?> SendAsync(NotificationChannel channel, string subject, string body, CancellationToken ct)
    {
        try
        {
            var secret = _secrets.UnprotectNullable(channel.EncryptedSecret);
            using var request = ChannelMessages.Build(channel, secret, subject, body);
            using var http = _http.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Notification through channel {Channel} failed.", channel.Name);
            // The message of a failed request can hold the address, and with it the secret of a webhook or the token of a bot.
            return ex is TaskCanceledException ? "no answer in 15 s" : ex is HttpRequestException h && h.StatusCode is { } code ? $"HTTP {(int)code}" : "not reachable";
        }
    }
}
