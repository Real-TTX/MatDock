using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MatDock.Core.Data;
using MatDock.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatDock.Core.Notifications.WebPush;

/// <summary>What a browser sends when it subscribes (<c>PushSubscription.toJSON()</c>).</summary>
public sealed record BrowserPushSubscription(string? Endpoint, BrowserPushKeys? Keys);

public sealed record BrowserPushKeys(string? P256dh, string? Auth);

public enum PushOutcome
{
    /// <summary>The push service took the message; it reaches the device when the device is online.</summary>
    Delivered,

    /// <summary>The subscription is gone (notifications switched off, the browser dropped it): the device is forgotten.</summary>
    Gone,

    /// <summary>Something else went wrong; the subscription may be fine.</summary>
    Failed,
}

/// <summary>
/// The devices of the users (Web Push subscriptions) and the sending to them: a notification is encrypted for each device (RFC 8291) and posted to its push
/// service signed as this installation (VAPID), as in Matmon. The connection goes to a public address only (<see cref="PublicNetwork"/>): the endpoint is what
/// a browser said, and without that check a signed-in user could make MatDock post into its own network by naming such an address.
/// </summary>
public sealed class WebPushService(MatDockDbContext db, WebPushKeys keys, IHttpClientFactory http, ILogger<WebPushService> logger)
{
    public const string HttpClientName = "matdock-webpush";

    public Task<bool> AnyAsync(CancellationToken ct = default) => db.PushDevices.AnyAsync(ct);

    public Task<List<PushDevice>> OfUserAsync(long userId, CancellationToken ct = default)
        => db.PushDevices.AsNoTracking().Where(d => d.UserId == userId).OrderBy(d => d.Name).ToListAsync(ct);

    /// <summary>Registers (or renews) the subscription of a browser for a user; null when it is not one that could work, else why not.</summary>
    public async Task<string?> RegisterAsync(long userId, BrowserPushSubscription subscription, string? userAgent, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || subscription.Endpoint!.Length > 1000)
        {
            return "The browser gave no https push address.";
        }

        if (WebPushEncryption.FromBase64Url(subscription.Keys?.P256dh) is not { Length: 65 } || WebPushEncryption.FromBase64Url(subscription.Keys?.Auth) is not { Length: 16 })
        {
            return "The browser gave no valid keys for its subscription.";
        }

        // A device switched off earlier keeps its (soft-deleted) row: it is switched on again rather than added twice under the same address.
        var device = await db.PushDevices.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Endpoint == subscription.Endpoint, ct);
        if (device is null)
        {
            device = new PushDevice { Endpoint = subscription.Endpoint };
            db.PushDevices.Add(device);
        }
        else if (device.UpdateState == UpdateState.Deleted)
        {
            device.UpdateState = UpdateState.Updated;
        }

        device.UserId = userId;
        device.P256dh = subscription.Keys!.P256dh!;
        device.Auth = subscription.Keys!.Auth!;
        device.Name = DeviceName(userAgent);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Removes a device of a user (by its push address or its id).</summary>
    public async Task<bool> RemoveAsync(long userId, string? endpoint, long? id, CancellationToken ct = default)
    {
        var device = await db.PushDevices.FirstOrDefaultAsync(d => d.UserId == userId && ((endpoint != null && d.Endpoint == endpoint) || (id != null && d.Id == id)), ct);
        if (device is null)
        {
            return false;
        }

        db.PushDevices.Remove(device);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Sends a notification to every device; forgets the ones that are gone. Answers an error for each device that failed.</summary>
    public async Task<List<string>> SendAllAsync(string title, string body, string url, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var device in await db.PushDevices.ToListAsync(ct))
        {
            switch (await SendAsync(device, title, body, url, ct))
            {
                case PushOutcome.Delivered:
                    device.LastUsedAt = DateTime.UtcNow;
                    break;
                case PushOutcome.Gone:
                    db.PushDevices.Remove(device);
                    break;
                default:
                    errors.Add($"Push to {device.Name}: the push service did not take it.");
                    break;
            }
        }

        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException ex) { logger.LogDebug(ex, "Push device state not saved."); }
        return errors;
    }

    public async Task<PushOutcome> SendAsync(PushDevice device, string title, string body, string url, CancellationToken ct = default)
    {
        var receiver = WebPushEncryption.FromBase64Url(device.P256dh);
        var auth = WebPushEncryption.FromBase64Url(device.Auth);
        if (receiver is not { Length: 65 } || auth is not { Length: 16 } || !Uri.TryCreate(device.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return PushOutcome.Gone;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { title = Trim(title, 120), body = Trim(body, 900), url, tag = Trim(title, 60) });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(WebPushEncryption.Encrypt(payload, receiver, auth)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("Authorization", keys.Authorization(device.Endpoint, "https://github.com/Real-TTX/MatDock", DateTimeOffset.UtcNow));
        request.Headers.TryAddWithoutValidation("TTL", "86400");
        request.Headers.TryAddWithoutValidation("Urgency", "normal");

        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                return PushOutcome.Delivered;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return PushOutcome.Gone;
            }

            logger.LogWarning("The push service {Host} answered {Status}.", endpoint.Host, (int)response.StatusCode);
            return PushOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("The push service {Host} could not be reached: {Reason}", endpoint.Host, ex.Message);
            return PushOutcome.Failed;
        }
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>A readable name for a device from its user agent: good enough to tell "my phone" from "my laptop".</summary>
    public static string DeviceName(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        var browser = ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera"
            : ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("SamsungBrowser/", StringComparison.Ordinal) ? "Samsung Internet"
            : ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : "Browser";
        var system = ua.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
            : ua.Contains("iPad", StringComparison.Ordinal) ? "iPad"
            : ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("Mac OS", StringComparison.Ordinal) ? "Mac"
            : ua.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : "a device";
        return $"{browser} on {system}";
    }
}
