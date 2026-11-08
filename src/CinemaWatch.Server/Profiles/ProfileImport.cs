using System.Text.Json;
using System.Text.RegularExpressions;
using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Server.Profiles;

/// <summary>A checked profile, ready to store, or the reasons it was refused (all of them, not only the first).</summary>
public sealed record ProfileCheck(string Name, WatchProfile? Profile, IReadOnlyList<string> Errors)
{
    public bool IsValid => Profile is not null && Errors.Count == 0;
}

/// <summary>
/// Checks an uploaded <c>*.cinemawatch.json</c> file. Stricter than the desktop's <see cref="ProfileStore.Load"/>,
/// which reads files from the user's own disk: here the file comes from a browser, its identifiers later go into
/// Cinema City request URLs and its booking links into e-mails (docs/SAAS.md §9).
/// </summary>
public static partial class ProfileImport
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxFilesPerImport = 10;
    public const int MaxTextLength = 200;
    public const int MaxIdLength = 32;
    public const int MaxSeatsPerScreening = 500;

    /// <summary>The desktop app's file name for a profile ("Diuna IMAX" → "Diuna IMAX.cinemawatch.json").</summary>
    public static string FileNameFor(string profileName) => ProfileStore.ToFileName(profileName) + ProfileStore.FileExtension;

    /// <param name="fileName">The uploaded file's name; gives the profile its name when the JSON has none.</param>
    /// <param name="polishNow">Current Polish wall-clock time (<see cref="PolishTime.Now"/>).</param>
    public static ProfileCheck Check(string fileName, string json, DateTime polishNow, int maxScreenings)
    {
        var fallbackName = NameFromFile(fileName);
        WatchProfile profile;
        try
        {
            profile = ProfileStore.Parse(json);
        }
        catch (InvalidDataException ex)
        {
            return new ProfileCheck(fallbackName, null, [ex.Message]);
        }
        catch (JsonException)
        {
            return new ProfileCheck(fallbackName, null,
                [T("To nie jest plik profilu CinemaWatch (niepoprawny JSON).", "This is not a CinemaWatch profile file (invalid JSON).")]);
        }

        var errors = new List<string>();
        Normalize(profile, fallbackName);

        CheckText(errors, T("Nazwa profilu", "Profile name"), profile.Name);
        CheckText(errors, T("Nazwa kina", "Cinema name"), profile.CinemaName);
        CheckText(errors, T("Nazwa filmu", "Film name"), profile.FilmName);
        CheckId(errors, T("Id kina", "Cinema id"), profile.CinemaId);
        foreach (var cinemaId in profile.CinemaIds)
            CheckId(errors, T("Id kina", "Cinema id"), cinemaId);
        if (profile.FilmId.Length > 0)
            CheckId(errors, T("Id filmu", "Film id"), profile.FilmId);
        if (profile.MinAdjacent < 1)
            errors.Add(T("Liczba miejsc obok siebie musi wynosić co najmniej 1.", "The number of adjacent seats must be at least 1."));
        if (profile.Screenings.Count > maxScreenings)
            errors.Add(T($"Za dużo seansów: {profile.Screenings.Count} (najwyżej {maxScreenings} w jednym profilu).",
                         $"Too many screenings: {profile.Screenings.Count} (at most {maxScreenings} in one profile)."));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < profile.Screenings.Count; i++)
        {
            var screening = profile.Screenings[i];
            var label = T($"Seans {(IdPattern().IsMatch(screening.Id) ? screening.Id : "nr " + (i + 1))}: ",
                          $"Screening {(IdPattern().IsMatch(screening.Id) ? screening.Id : "no. " + (i + 1))}: ");
            CheckScreening(errors, label, screening);
            if (screening.Id.Length > 0 && !ids.Add(screening.Id))
                errors.Add(label + T("występuje w profilu dwa razy.", "appears twice in the profile."));
        }

        if (errors.Count == 0 && !profile.Screenings.Any(s => s.StartsAt > polishNow))
            errors.Add(T("Wszystkie seanse tego profilu już się zaczęły.", "All screenings of this profile have already started."));

        return new ProfileCheck(profile.Name, errors.Count == 0 ? profile : null, errors);
    }

    /// <summary>Start of the profile's last screening as UTC (screening times are Polish wall-clock times).</summary>
    public static DateTimeOffset LastStartUtc(WatchProfile profile) =>
        new(TimeZoneInfo.ConvertTimeToUtc(profile.Screenings.Max(s => s.StartsAt), PolishTime.Zone), TimeSpan.Zero);

    /// <summary>A refusal that happens before the content is read (e.g. a file that is too large).</summary>
    public static ProfileCheck Refuse(string fileName, string error) => new(NameFromFile(fileName), null, [error]);

    /// <summary>"C:\fakepath\Diuna IMAX.cinemawatch.json" → "Diuna IMAX"; never empty.</summary>
    private static string NameFromFile(string fileName)
    {
        var name = ProfileStore.NameFromPath(Path.GetFileName(fileName.Replace('\\', '/'))).Trim();
        if (name.Length > MaxTextLength)
            name = name[..MaxTextLength];
        return name.Length > 0 && !name.Any(char.IsControl) ? name : T("profil", "profile");
    }

    /// <summary>JSON nulls become empty values, so the stored document reads back like one saved by the desktop app.</summary>
    private static void Normalize(WatchProfile profile, string fallbackName)
    {
        profile.Name = (profile.Name ?? "").Trim();
        if (profile.Name.Length == 0)
            profile.Name = fallbackName;
        profile.CinemaName ??= "";
        profile.FilmId ??= "";
        profile.FilmName ??= "";
        profile.CinemaIds = (profile.CinemaIds ?? []).Select(id => id ?? "").ToList();
        profile.Screenings = profile.Screenings.Select(s => s ?? new WatchedScreening()).ToList();
        foreach (var screening in profile.Screenings)
        {
            screening.Id ??= "";
            screening.CinemaId ??= "";
            screening.City ??= "";
            screening.Auditorium ??= "";
            screening.BookingLink ??= "";
            screening.SeatKeys = (screening.SeatKeys ?? []).Select(key => key ?? "").Distinct(StringComparer.Ordinal).ToList();
        }
    }

    private static void CheckScreening(List<string> errors, string label, WatchedScreening screening)
    {
        if (!IdPattern().IsMatch(screening.Id))
            errors.Add(label + T("brak id albo niedozwolone znaki w id (dozwolone litery i cyfry).",
                                 "missing id or not allowed characters in the id (letters and digits only)."));
        if (screening.CinemaId.Length > 0)
            CheckId(errors, label + T("id kina", "cinema id"), screening.CinemaId);
        CheckText(errors, label + T("miasto", "city"), screening.City);
        CheckText(errors, label + T("sala", "hall"), screening.Auditorium);

        if (screening.StartsAt == default || screening.StartsAt.Kind != DateTimeKind.Unspecified || PolishTime.Zone.IsInvalidTime(screening.StartsAt))
            errors.Add(label + T("nieprawidłowy czas seansu (oczekiwany czas polski bez strefy, jak zapisuje aplikacja).",
                                 "invalid start time (expected Polish time without an offset, as the app saves it)."));

        if (screening.SeatKeys.Count == 0)
            errors.Add(label + T("brak pilnowanych miejsc.", "no watched seats."));
        else if (screening.SeatKeys.Count > MaxSeatsPerScreening)
            errors.Add(label + T($"za dużo miejsc ({screening.SeatKeys.Count}).", $"too many seats ({screening.SeatKeys.Count})."));
        else if (screening.SeatKeys.FirstOrDefault(key => !SeatKeyPattern().IsMatch(key)) is { } bad)
            errors.Add(label + T($"nieprawidłowy klucz miejsca „{Shorten(bad)}”.", $"invalid seat key \"{Shorten(bad)}\"."));

        if (!IsCinemaCityLink(screening.BookingLink))
            errors.Add(label + T("link do zakupu musi prowadzić do https://…cinema-city.pl.",
                                 "the booking link must lead to https://…cinema-city.pl."));
    }

    /// <summary>Booking links go into e-mails: https on cinema-city.pl or one of its subdomains, nothing else.</summary>
    public static bool IsCinemaCityLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
        && uri.IsDefaultPort
        && (uri.Host == "cinema-city.pl" || uri.Host.EndsWith(".cinema-city.pl", StringComparison.Ordinal));

    private static void CheckText(List<string> errors, string field, string value)
    {
        if (value.Length > MaxTextLength)
            errors.Add(T($"{field}: za długi tekst (najwyżej {MaxTextLength} znaków).", $"{field}: text too long (at most {MaxTextLength} characters)."));
        else if (value.Any(char.IsControl))
            errors.Add(T($"{field}: niedozwolone znaki sterujące.", $"{field}: control characters are not allowed."));
    }

    private static void CheckId(List<string> errors, string field, string value)
    {
        if (!IdPattern().IsMatch(value))
            errors.Add(T($"{field}: brak albo niedozwolone znaki (dozwolone litery i cyfry).", $"{field}: missing or not allowed characters (letters and digits only)."));
    }

    private static string Shorten(string value) => value.Length <= 20 ? value : value[..20] + "…";

    /// <summary>A screening, film or cinema id as the import accepts it: letters and digits, 1-32 characters.</summary>
    public static bool IsValidId(string? value) => value is not null && IdPattern().IsMatch(value);

    /// <summary>
    /// Identifiers go into request URLs (presentations/{id}); letters and digits keep anything else out. The patterns
    /// end with \z, not $: $ would also accept a trailing line break.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9]{1,32}\z")]
    private static partial Regex IdPattern();

    /// <summary>section_position_rowKey (docs/KNOWLEDGE_BASE.md §3.4).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9]{1,10}_[A-Za-z0-9]{1,10}_[A-Za-z0-9]{1,10}\z")]
    private static partial Regex SeatKeyPattern();
}
