namespace MatDock.Core.Entities;

/// <summary>Where a notification can go besides e-mail and the generic webhook.</summary>
public enum NotificationChannelType
{
    /// <summary>A Discord webhook (the secret is its address).</summary>
    Discord = 0,

    /// <summary>A Slack incoming webhook (the secret is its address).</summary>
    Slack = 1,

    /// <summary>A Microsoft Teams workflow webhook ("When a Teams webhook request is received"; the secret is its address).</summary>
    Teams = 2,

    /// <summary>A Telegram bot: the secret is the bot token, <see cref="NotificationChannel.Target"/> the chat id.</summary>
    Telegram = 3,

    /// <summary>An ntfy server (<see cref="NotificationChannel.Url"/>, ntfy.sh by default) and topic; the secret is an optional access token.</summary>
    Ntfy = 4,

    /// <summary>A Gotify server (<see cref="NotificationChannel.Url"/>); the secret is the token of an application.</summary>
    Gotify = 5,
}

/// <summary>
/// A channel every notification goes to, next to e-mail and the webhook of <see cref="NotificationSettings"/>. Its secret (a webhook address, a bot
/// or application token) is stored encrypted. Mapped to the <c>NotificationChannel</c> table.
/// </summary>
public class NotificationChannel : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    public NotificationChannelType Type { get; set; }

    /// <summary>The server of ntfy and Gotify (empty = ntfy.sh for ntfy).</summary>
    public string? Url { get; set; }

    /// <summary>The chat id of Telegram, the topic of ntfy.</summary>
    public string? Target { get; set; }

    /// <summary>Data-Protection-encrypted secret: the webhook address (Discord, Slack, Teams), the bot token (Telegram), the token (Gotify, ntfy).</summary>
    public string? EncryptedSecret { get; set; }

    public bool Enabled { get; set; } = true;
}
