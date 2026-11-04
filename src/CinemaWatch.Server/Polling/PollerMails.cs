using System.Text;
using CinemaWatch.Core;
using CinemaWatch.Server.Mail;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Server.Polling;

/// <summary>
/// Texts of the poller's e-mails, in the language of the current flow (<see cref="Localizer.Use"/>): the notification
/// about freed seats (also as a test e-mail) and the owner's alerts about a block and its end.
/// </summary>
public static class PollerMails
{
    /// <summary>Where the test e-mail's sample link leads: Cinema City's home page.</summary>
    public const string CinemaCityHome = "https://www.cinema-city.pl/";

    /// <summary>"Wolne miejsca: Diuna: Część trzecia" with one block per screening and its booking link.</summary>
    public static OutgoingMail SeatsAvailable(
        string to, WatchProfile profile, string profileName,
        IReadOnlyList<(WatchedScreening Screening, IReadOnlyList<Seat> Seats)> news, string profileLink)
    {
        var film = string.IsNullOrWhiteSpace(profile.FilmName) ? profileName : profile.FilmName;
        var lines = news
            .OrderBy(n => n.Screening.StartsAt)
            .Select(n => (ScreeningLine(n.Screening, n.Seats), n.Screening.BookingLink))
            .ToList();
        return Notification(to, $"{T("Wolne miejsca", "Seats available")}: {film}",
            T($"Zwolniły się pilnowane miejsca (profil „{profileName}”):", $"Watched seats have become free (profile \"{profileName}\"):"),
            lines, profileLink);
    }

    /// <summary>The same layout with a sample screening, to check delivery and the spam folder.</summary>
    public static OutgoingMail Test(string to, DateTime polishNow, string siteLink)
    {
        var sample = polishNow.Date.AddDays(1).AddHours(19);
        var line = $"{T("Warszawa", "Warsaw")} · {sample.ToString(T("ddd d.MM, HH:mm", "ddd d MMM, HH:mm"), Culture)} · {T("rząd", "row")} 8: 12, 13";
        return Notification(to, $"[TEST] {T("Wolne miejsca", "Seats available")}: {T("Przykładowy film", "Sample film")}",
            T("To jest wiadomość testowa CinemaWatch: tak wygląda powiadomienie o wolnych miejscach. Jeśli trafiła do spamu, oznacz ją jako „to nie spam”, żeby prawdziwe powiadomienia trafiały do skrzynki.",
              "This is a CinemaWatch test message: this is what a notification about free seats looks like. If it landed in spam, mark it as \"not spam\" so that real notifications reach your inbox."),
            [(line, CinemaCityHome)], siteLink);
    }

    /// <summary>"Warszawa · pt 21.11, 19:00 · rząd 8: 12, 13"</summary>
    public static string ScreeningLine(WatchedScreening screening, IReadOnlyList<Seat> seats)
    {
        var city = Locations.FindCinema(screening.CinemaId)?.DisplayCity ?? screening.City;
        var when = screening.StartsAt.ToString(T("ddd d.MM, HH:mm", "ddd d MMM, HH:mm"), Culture);
        return (string.IsNullOrWhiteSpace(city) ? "" : $"{city} · ") + $"{when} · {NotificationBuilder.DescribeSeats(seats)}";
    }

    public static OutgoingMail Blocked(string to, string reason, DateTimeOffset until, int blocksInARow, string siteLink)
    {
        var untilText = PolishTime.FromUtc(until.UtcDateTime).ToString("d.MM.yyyy HH:mm", Culture);
        var paragraphs = new[]
        {
            T($"Cinema City odrzuciło zapytanie serwera: {reason}.", $"Cinema City refused a request from the server: {reason}."),
            T($"Serwer nie wysyła żadnych zapytań do {untilText} (czasu polskiego). Potem spróbuje jednym zapytaniem; gdy przejdzie, sprawdzanie wróci do normy i dostaniesz e-mail.",
              $"The server sends no requests until {untilText} (Polish time). Then it tries one request; once it gets through, checks return to normal and you get an e-mail."),
            T($"To blokada nr {blocksInARow} z rzędu; każda kolejna wydłuża pauzę dwukrotnie. Użytkownicy widzą przerwę na stronie profili.",
              $"This is block no. {blocksInARow} in a row; each further one doubles the pause. Users see the pause on the profiles page."),
        };
        return Plain(to, T("CinemaWatch: sprawdzanie wstrzymane", "CinemaWatch: checks paused"), paragraphs, siteLink);
    }

    public static OutgoingMail Resumed(string to, DateTimeOffset? blockedAt, string siteLink)
    {
        var since = blockedAt is { } at ? PolishTime.FromUtc(at.UtcDateTime).ToString("d.MM.yyyy HH:mm", Culture) : null;
        var paragraphs = new[]
        {
            T("Serwer znowu dostaje odpowiedzi z Cinema City: sprawdzanie działa normalnie.", "The server gets answers from Cinema City again: checks are back to normal."),
            since is null ? "" : T($"Blokada zaczęła się {since} (czasu polskiego).", $"The block started at {since} (Polish time)."),
        };
        return Plain(to, T("CinemaWatch: sprawdzanie wznowione", "CinemaWatch: checks resumed"), paragraphs.Where(p => p.Length > 0).ToArray(), siteLink);
    }

    private static OutgoingMail Notification(string to, string subject, string intro, IReadOnlyList<(string Line, string Link)> lines, string profileLink)
    {
        var buy = T("Kup bilet", "Buy a ticket");
        var hurry = T("Zwolnione miejsca szybko znikają.", "Freed seats do not stay free for long.");
        var profileText = T("Profil w CinemaWatch", "The profile in CinemaWatch");
        var stop = T("Nie chcesz więcej takich e-maili? Wyłącz profil na stronie CinemaWatch.", "No more e-mails like this? Disable the profile on the CinemaWatch site.");

        var text = new StringBuilder().Append(intro).Append("\n\n");
        foreach (var (line, link) in lines)
            text.Append(line).Append('\n').Append(buy).Append(": ").Append(link).Append("\n\n");
        text.Append(hurry).Append('\n').Append(profileText).Append(": ").Append(profileLink).Append("\n\n").Append(stop).Append('\n');

        var html = new MailHtml().Paragraph(intro);
        foreach (var (line, link) in lines)
            html.Lines((line, false), (MailHtml.Link(link, buy), true));
        html.Paragraph(hurry).Lines((MailHtml.Link(profileLink, profileText), true)).Paragraph(stop);
        return new OutgoingMail(to, subject, text.ToString(), html.ToString());
    }

    private static OutgoingMail Plain(string to, string subject, IReadOnlyList<string> paragraphs, string siteLink)
    {
        var text = string.Join("\n\n", paragraphs) + $"\n\n{siteLink}\n";
        var html = new MailHtml();
        foreach (var paragraph in paragraphs)
            html.Paragraph(paragraph);
        html.Raw($"<p>{MailHtml.Link(siteLink, "CinemaWatch")}</p>");
        return new OutgoingMail(to, subject, text, html.ToString());
    }
}
