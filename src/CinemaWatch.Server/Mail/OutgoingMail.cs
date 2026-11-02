namespace CinemaWatch.Server.Mail;

/// <summary>One e-mail with a plain-text and an HTML version of the same content.</summary>
public sealed record OutgoingMail(string To, string Subject, string Text, string Html);

/// <summary>Sends e-mails. SMTP in the app (<see cref="SmtpMailSender"/>); a recording fake in the tests.</summary>
public interface IMailSender
{
    Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default);
}
