using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Core;

/// <summary>Cinema location. <see cref="City"/> is Polish; <see cref="DisplayCity"/> follows the UI language.</summary>
public sealed record Cinema(string Id, string Name, string City, string? CityEnglish = null)
{
    public string DisplayCity => T(City, CityEnglish ?? City);
}

/// <summary>
/// Cinemas supported by the app: the IMAX halls of Cinema City, which has exclusive IMAX rights in Poland.
/// The list is fixed (checked in October 2026 against the Cinema City cinema list and repertoires) because finding
/// IMAX halls dynamically would cost one request per cinema on every start; a new hall means a small update.
/// </summary>
public static class Locations
{
    /// <summary>Cinema City tenant id for Poland.</summary>
    public const string TenantPoland = "10103";

    public const string ImaxTechnology = "IMAX";

    public static Cinema Sadyba { get; } = new("1060", "Cinema City Sadyba", "Warszawa", "Warsaw");

    /// <summary>IMAX cinemas, ordered by city.</summary>
    public static IReadOnlyList<Cinema> ImaxCinemas { get; } =
    [
        new("1065", "Cinema City Punkt 44", "Katowice"),
        new("1064", "Cinema City Zakopianka", "Kraków"),
        new("1080", "Cinema City Manufaktura", "Łódź"),
        new("1078", "Cinema City Plaza", "Poznań"),
        Sadyba,
        new("1097", "Cinema City Wroclavia", "Wrocław"),
    ];

    public static Cinema? FindCinema(string? id) => ImaxCinemas.FirstOrDefault(c => c.Id == id);
}
