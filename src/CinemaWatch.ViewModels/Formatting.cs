using System.Globalization;
using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>Display formatting in the current UI language (the screening texts live in Core, shared with the site).</summary>
public static class Formatting
{
    public static CultureInfo Culture => Localizer.Culture;

    /// <summary>"Wtorek, 15 grudnia 2026" / "Tuesday, 15 December 2026"</summary>
    public static string DayHeader(DateOnly date) => ScreeningText.DayHeader(date);

    /// <summary>"wt. 15.12, 16:00" / "Tue 15 Dec, 16:00"</summary>
    public static string ShortDateTime(DateTime value) => value.ToString(T("ddd d.MM, HH:mm", "ddd d MMM, HH:mm"), Culture);

    /// <summary>"15.12" / "15 Dec"</summary>
    public static string ShortDate(DateOnly value) => ScreeningText.ShortDate(value);

    public static string Time(DateTime value) => ScreeningText.Time(value);

    /// <summary>"EN · napisy PL" / "EN · PL subtitles"</summary>
    public static string Languages(Screening screening) => ScreeningText.Languages(screening);

    /// <inheritdoc cref="ScreeningText.Availability"/>
    public static string Availability(Screening screening) => ScreeningText.Availability(screening);

    public static bool IsNearlySoldOut(Screening screening) => ScreeningText.IsNearlySoldOut(screening);
}
