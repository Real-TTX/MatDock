using MailKit.Security;
using MatDock.Core.Entities;

namespace MatDock.Core.Notifications;

/// <summary>How the connection to the mail server is secured.</summary>
public enum MailSecurity
{
    /// <summary>Plain SMTP.</summary>
    None,

    /// <summary>Plain connection, then <c>STARTTLS</c> (required).</summary>
    StartTls,

    /// <summary>TLS from the first byte.</summary>
    Tls,
}

/// <summary>
/// A mail relay set from outside MatDock: MatSMTP, which Mattiverse provides through the <c>MATSMTP_*</c> variables
/// (MatDesign docs/PLATFORM.md "Mail through MatSMTP"). The web layer reads the variables; Core only sees this.
/// </summary>
public sealed record MailRelay(string Host, int Port, MailSecurity Security, string? Username, string? Password, string? From, string Description);

/// <summary>Where the relay comes from (the environment); <see cref="Current"/> is null when there is none.</summary>
public interface IMailRelaySource
{
    MailRelay? Current { get; }
}

/// <summary>No relay: MatDock uses its own mail settings.</summary>
public sealed class NoMailRelay : IMailRelaySource
{
    public MailRelay? Current => null;
}

/// <summary>Everything the mail client needs for one message.</summary>
public sealed record MailClientSettings(string? Host, int Port, MailSecurity Security, string? Username, string? Password, string? From, bool ThroughRelay)
{
    public SecureSocketOptions SocketOptions => Security switch
    {
        MailSecurity.Tls => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };
}

/// <summary>
/// Which mail server MatDock's mail goes to. With a relay (MatSMTP) all mail goes through it and the server settings (host, port,
/// security, user, password, sender) are fixed: shown, not editable, nothing saved for them; what MatDock stored for its own
/// server stays stored and counts again when the relay is gone. Whether mail is on and the recipients stay MatDock's own.
/// </summary>
public static class MailServer
{
    /// <summary>The client settings for a message: the relay's when there is one, else MatDock's own.</summary>
    public static MailClientSettings Compose(NotificationSettings own, string? ownPassword, MailRelay? relay)
    {
        if (relay is not null)
        {
            var hasUser = !string.IsNullOrEmpty(relay.Username);
            return new MailClientSettings(relay.Host, relay.Port, relay.Security, hasUser ? relay.Username : null,
                hasUser ? relay.Password ?? string.Empty : null, relay.From ?? own.SmtpFrom, ThroughRelay: true);
        }

        // "Use TLS" was STARTTLS with System.Net.Mail; port 465 only speaks TLS from the first byte.
        var security = !own.SmtpUseTls ? MailSecurity.None : own.SmtpPort == 465 ? MailSecurity.Tls : MailSecurity.StartTls;
        var user = string.IsNullOrWhiteSpace(own.SmtpUsername) ? null : own.SmtpUsername;
        return new MailClientSettings(own.SmtpHost, own.SmtpPort, security, user, user is null ? null : ownPassword ?? string.Empty, own.SmtpFrom,
            ThroughRelay: false);
    }

    /// <summary>
    /// What to save from <paramref name="input"/>: with a relay its server fields are replaced by the <paramref name="stored"/> ones and the
    /// password is left as it is, so a change of them is never saved; without one <paramref name="input"/> as it is.
    /// </summary>
    public static NotificationSettingsInput KeepFixed(NotificationSettingsInput input, NotificationSettings stored, MailRelay? relay)
    {
        if (relay is null)
        {
            return input;
        }

        input.SmtpHost = stored.SmtpHost;
        input.SmtpPort = stored.SmtpPort;
        input.SmtpUseTls = stored.SmtpUseTls;
        input.SmtpUsername = stored.SmtpUsername;
        input.SmtpFrom = stored.SmtpFrom;
        input.SmtpPassword = null;
        input.ClearSmtpPassword = false;
        return input;
    }
}
