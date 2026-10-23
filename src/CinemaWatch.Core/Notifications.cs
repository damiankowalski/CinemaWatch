using System.Xml.Linq;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Core;

/// <summary>Content of a "seats available" notification.</summary>
public sealed record AvailabilityNotification(string Title, IReadOnlyList<string> Lines, string? LaunchUrl);

/// <summary>Shows notifications (Windows toast in the app; a fake in tests).</summary>
public interface INotifier
{
    void Show(AvailabilityNotification notification);
}

public static class NotificationBuilder
{
    private const int MaxScreeningLines = 3;

    public static AvailabilityNotification Build(
        WatchProfile profile, IReadOnlyList<(WatchedScreening Screening, IReadOnlyList<Seat> Seats)> matches)
    {
        var ordered = matches.OrderBy(m => m.Screening.StartsAt).ToList();
        var lines = ordered
            .Take(MaxScreeningLines)
            .Select(m => CityPrefix(m.Screening) + $"{m.Screening.StartsAt.ToString(T("ddd d.MM, HH:mm", "ddd d MMM, HH:mm"), Culture)} · {DescribeSeats(m.Seats)}")
            .ToList();
        var rest = ordered.Count - MaxScreeningLines;
        if (rest > 0)
            lines.Add("+ " + Count(rest, "kolejny seans", "kolejne seanse", "kolejnych seansów", "more screening", "more screenings"));

        var film = string.IsNullOrWhiteSpace(profile.FilmName) ? profile.Name : profile.FilmName;
        var url = ordered.Select(m => m.Screening.BookingLink).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return new AvailabilityNotification($"{T("Wolne miejsca", "Seats available")}: {film}", lines, url);
    }

    /// <summary>"Kraków · " for screenings that know their cinema (profiles from 1.4 on).</summary>
    private static string CityPrefix(WatchedScreening screening)
    {
        var cinema = Locations.FindCinema(screening.CinemaId);
        var city = cinema?.DisplayCity ?? screening.City;
        return string.IsNullOrWhiteSpace(city) ? "" : $"{city} · ";
    }

    /// <summary>"rząd 8: 12, 13 · rząd 9: 5" / "row 8: 12, 13 · row 9: 5"</summary>
    public static string DescribeSeats(IEnumerable<Seat> seats) =>
        string.Join(" · ", seats
            .GroupBy(s => (s.Section, s.RowKey))
            .OrderBy(g => g.Average(s => s.Y))
            .Select(g => $"{T("rząd", "row")} {g.First().Row}: {SeatSelectionText.CompressLabels(g.Select(s => s.Label))}"));
}

/// <summary>Builds Windows toast XML. Clicking the toast or the button opens the booking page (protocol activation).</summary>
public static class ToastXml
{
    public static string Build(AvailabilityNotification notification)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", notification.Title));
        if (notification.Lines.Count > 0)
            binding.Add(new XElement("text", string.Join("\n", notification.Lines.Take(2))));
        if (notification.Lines.Count > 2)
            binding.Add(new XElement("text", string.Join("\n", notification.Lines.Skip(2))));

        var toast = new XElement("toast", new XElement("visual", binding));
        if (!string.IsNullOrWhiteSpace(notification.LaunchUrl))
        {
            toast.Add(new XAttribute("activationType", "protocol"), new XAttribute("launch", notification.LaunchUrl));
            toast.Add(new XElement("actions",
                new XElement("action",
                    new XAttribute("content", T("Kup bilet", "Buy ticket")),
                    new XAttribute("activationType", "protocol"),
                    new XAttribute("arguments", notification.LaunchUrl))));
        }
        return toast.ToString(SaveOptions.DisableFormatting);
    }
}
