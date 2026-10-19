using MatDock.Core.Notifications;
using MatPlatform;

namespace MatDock.Web.Platform;

/// <summary>MatSMTP as MatDock's mail relay when Mattiverse set the <c>MATSMTP_*</c> variables (MatPlatform <see cref="MatSmtpSettings"/>).</summary>
public sealed class MatSmtpRelaySource(MatSmtpSettings smtp) : IMailRelaySource
{
    public MailRelay? Current => smtp.IsConfigured
        ? new MailRelay(
            smtp.Host!,
            smtp.Port,
            smtp.Tls switch
            {
                MatSmtpTls.Tls => MailSecurity.Tls,
                MatSmtpTls.StartTls => MailSecurity.StartTls,
                _ => MailSecurity.None,
            },
            smtp.HasCredentials ? smtp.Username : null,
            smtp.HasCredentials ? smtp.Password : null,
            smtp.FromAddress,
            smtp.Describe("en")!)
        : null;
}
