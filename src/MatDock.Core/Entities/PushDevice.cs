namespace MatDock.Core.Entities;

/// <summary>
/// A browser (or the installed MatDock app on a phone) that a user switched notifications on in: a Web Push subscription. It belongs to the user who
/// registered it and to this installation (its VAPID key pair, <see cref="Notifications.WebPush.WebPushKeys"/>). Every notification of MatDock goes to
/// the devices too.
/// </summary>
public class PushDevice : AuditableEntity
{
    public long UserId { get; set; }

    /// <summary>The push service's address for this browser (https; Google, Mozilla, Apple, Microsoft).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The browser's public key (base64url, an uncompressed P-256 point): what a message is encrypted for.</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>The subscription's auth secret (base64url, 16 bytes).</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>What the user sees in the list: "Chrome on Android", "Safari on iPhone".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When a notification last reached the push service for it.</summary>
    public DateTime? LastUsedAt { get; set; }
}
