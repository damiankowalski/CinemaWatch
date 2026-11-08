using CinemaWatch.Server.Mail;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Server.Accounts;

/// <summary>Texts of the account e-mails, in the language of the current flow (<see cref="CinemaWatch.Core.Localizer.Use"/>).</summary>
public static class AccountMails
{
    public static OutgoingMail Invitation(string to, string link, TimeSpan validFor) => Build(
        to,
        T("Zaproszenie do CinemaWatch", "Your invitation to CinemaWatch"),
        T("Masz zaproszenie do CinemaWatch: powiadomień o miejscach, które zwalniają się na wyprzedane seanse w Cinema City.",
          "You have been invited to CinemaWatch: notifications about seats that free up for sold-out Cinema City screenings."),
        T("Ustaw hasło, aby aktywować konto:", "Set a password to activate your account:"),
        T("Ustaw hasło", "Set password"),
        link,
        T($"Link jest ważny {Hours(validFor)}. Gdy wygaśnie, wybierz „Nie pamiętasz hasła?” na stronie logowania, a dostaniesz nowy. Jeśli nie spodziewasz się tej wiadomości, zignoruj ją.",
          $"The link is valid for {Hours(validFor)}. If it expires, choose “Forgot your password?” on the sign-in page to get a new one. If you did not expect this message, ignore it."));

    public static OutgoingMail PasswordReset(string to, string link, TimeSpan validFor) => Build(
        to,
        T("Nowe hasło do CinemaWatch", "New password for CinemaWatch"),
        T("Ktoś, zapewne Ty, poprosił o nowe hasło do CinemaWatch.", "Someone, probably you, asked for a new CinemaWatch password."),
        T("Ustaw nowe hasło:", "Set a new password:"),
        T("Ustaw nowe hasło", "Set new password"),
        link,
        T($"Link jest ważny {Hours(validFor)}. Jeśli to nie Ty, zignoruj tę wiadomość: obecne hasło dalej działa.",
          $"The link is valid for {Hours(validFor)}. If this was not you, ignore this message: your current password still works."));

    /// <summary>To the person whose account the owner deleted (active accounts only; a withdrawn invitation gets nothing).</summary>
    public static OutgoingMail AccountDeleted(string to)
    {
        var subject = T("Twoje konto CinemaWatch zostało usunięte", "Your CinemaWatch account has been deleted");
        var what = T("Właściciel CinemaWatch usunął Twoje konto razem z profilami i historią powiadomień. Nie dostaniesz już powiadomień o zwolnionych miejscach.",
                     "The owner of CinemaWatch has deleted your account together with its profiles and notification history. You will not get seat notifications any more.");
        var backups = T("Zaszyfrowane kopie zapasowe bazy znikają po najwyżej 31 dniach.",
                        "Encrypted database backups are gone after 31 days at most.");
        var footer = T("Jeśli to pomyłka, skontaktuj się z osobą, która Cię zaprosiła. Na tę wiadomość nie trzeba odpowiadać.",
                       "If this is a mistake, contact the person who invited you. There is no need to reply to this message.");
        var text = $"{what}\n\n{backups}\n\n{footer}\n";
        var html = new MailHtml().Paragraph(what).Paragraph(backups).Paragraph(footer).ToString();
        return new OutgoingMail(to, subject, text, html);
    }

    private static string Hours(TimeSpan span) =>
        Count((int)Math.Round(span.TotalHours), "godzinę", "godziny", "godzin", "hour", "hours");

    private static OutgoingMail Build(string to, string subject, string intro, string action, string button, string link, string footer)
    {
        var text = $"{intro}\n\n{action}\n{link}\n\n{footer}\n";
        var html = new MailHtml()
            .Paragraph(intro)
            .Paragraph(action)
            .Raw($"<p>{MailHtml.Link(link, button)}</p>")
            .Paragraph(footer)
            .ToString();
        return new OutgoingMail(to, subject, text, html);
    }
}
