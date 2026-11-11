using System.Globalization;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Core;

/// <summary>
/// Texts about screenings in the current UI language, shared by the desktop app and the site's wizard (so both show
/// the same day headers, languages and availability).
/// </summary>
public static class ScreeningText
{
    /// <summary>"Wtorek, 15 grudnia 2026" / "Tuesday, 15 December 2026"</summary>
    public static string DayHeader(DateOnly date) => Capitalize(date.ToString("dddd, d MMMM yyyy", Culture));

    /// <summary>"15.12" / "15 Dec"</summary>
    public static string ShortDate(DateOnly value) => value.ToString(T("d.MM", "d MMM"), Culture);

    public static string Time(DateTime value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"EN · napisy PL" / "EN · PL subtitles"</summary>
    public static string Languages(Screening screening)
    {
        var parts = new List<string>();
        if (screening.OriginalLanguages.Count > 0)
            parts.Add(Join(screening.OriginalLanguages));
        if (screening.SubtitleLanguages.Count > 0)
            parts.Add(T($"napisy {Join(screening.SubtitleLanguages)}", $"{Join(screening.SubtitleLanguages)} subtitles"));
        if (screening.DubbedLanguages.Count > 0)
            parts.Add(T($"dubbing {Join(screening.DubbedLanguages)}", $"{Join(screening.DubbedLanguages)} dubbing"));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Availability from the repertoire. Its ratio also counts wheelchair spaces, so a value below 1%
    /// usually means "practically sold out" (e.g. 2 of 385 = only the wheelchair spaces left).
    /// </summary>
    public static string Availability(Screening screening)
    {
        if (screening.SoldOut) return T("Wyprzedany", "Sold out");
        if (screening.AvailabilityRatio <= 0) return T("Brak wolnych miejsc", "No free seats");
        if (IsNearlySoldOut(screening)) return T("Poniżej 1% miejsc wolnych", "Under 1% of seats free");
        var percent = (int)Math.Round(screening.AvailabilityRatio * 100);
        return T($"{percent}% miejsc wolnych", $"{percent}% of seats free");
    }

    public static bool IsNearlySoldOut(Screening screening) =>
        screening.SoldOut || screening.AvailabilityRatio < 0.01;

    private static string Join(IEnumerable<string> languages) => string.Join("/", languages).ToUpperInvariant();

    private static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], Culture) + text[1..];
}
