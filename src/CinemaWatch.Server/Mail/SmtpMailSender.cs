using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace CinemaWatch.Server.Mail;

/// <summary>
/// Sends e-mails over SMTP. Every provider considered for production (docs/SAAS.md §8.3) offers SMTP, so changing
/// the provider is a configuration change; during development the target is a local test inbox (smtp4dev).
/// </summary>
public sealed class SmtpMailSender(IOptions<MailOptions> options, ILogger<SmtpMailSender> logger) : IMailSender
{
    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(mail.To));
        message.Subject = mail.Subject;
        message.Body = new BodyBuilder { TextBody = mail.Text, HtmlBody = mail.Html }.ToMessageBody();

        var security = settings.Security switch
        {
            MailSecurity.StartTls => SecureSocketOptions.StartTls,
            MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.None,
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(settings.UserName))
            await client.AuthenticateAsync(settings.UserName, settings.Password ?? "", cancellationToken).ConfigureAwait(false);
        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);

        // The address is personal data: the log gets the subject only.
        logger.LogInformation("E-mail sent: {Subject}", mail.Subject);
    }
}
