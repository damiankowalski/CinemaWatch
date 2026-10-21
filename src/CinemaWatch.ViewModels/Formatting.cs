using System.Globalization;
using CinemaWatch.Core;

namespace CinemaWatch.ViewModels;

/// <summary>Polish UI text helpers.</summary>
public static class Formatting
{
    public static CultureInfo Polish { get; } = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>"Wtorek, 15 grudnia 2026"</summary>
    public static string DayHeader(DateOnly date) => Capitalize(date.ToString("dddd, d MMMM yyyy", Polish));

    /// <summary>"wt. 15.12, 16:00"</summary>
    public static string ShortDateTime(DateTime value) => value.ToString("ddd d.MM, HH:mm", Polish);

    public static string Time(DateTime value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"EN · napisy PL"</summary>
    public static string Languages(Screening screening)
    {
        var parts = new List<string>();
        if (screening.OriginalLanguages.Count > 0)
            parts.Add(string.Join("/", screening.OriginalLanguages).ToUpperInvariant());
        if (screening.SubtitleLanguages.Count > 0)
            parts.Add("napisy " + string.Join("/", screening.SubtitleLanguages).ToUpperInvariant());
        if (screening.DubbedLanguages.Count > 0)
            parts.Add("dubbing " + string.Join("/", screening.DubbedLanguages).ToUpperInvariant());
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Availability from the repertoire. Its ratio also counts wheelchair spaces, so a value below 1%
    /// usually means "practically sold out" (e.g. 2 of 385 = only the wheelchair spaces left).
    /// </summary>
    public static string Availability(Screening screening)
    {
        if (screening.SoldOut) return "Wyprzedany";
        if (screening.AvailabilityRatio <= 0) return "Brak wolnych miejsc";
        if (IsNearlySoldOut(screening)) return "Poniżej 1% miejsc wolnych";
        return $"{(int)Math.Round(screening.AvailabilityRatio * 100)}% miejsc wolnych";
    }

    public static bool IsNearlySoldOut(Screening screening) =>
        screening.SoldOut || screening.AvailabilityRatio < 0.01;

    /// <summary>Polish plural form: 1 seans, 2 seanse, 5 seansów.</summary>
    public static string Plural(int count, string one, string few, string many)
    {
        if (count == 1) return one;
        var lastDigit = count % 10;
        var lastTwo = count % 100;
        return lastDigit is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
    }

    public static string Count(int count, string one, string few, string many) => $"{count} {Plural(count, one, few, many)}";

    private static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], Polish) + text[1..];
}
