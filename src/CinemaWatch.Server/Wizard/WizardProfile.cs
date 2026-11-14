using CinemaWatch.Core;
using CinemaWatch.Server.Profiles;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Server.Wizard;

/// <summary>A screening on step 4: what the profile will store for it (seat keys sorted, as the desktop saves them).</summary>
public sealed record DraftScreening(WatchedScreening Screening, int SeatCount);

/// <summary>
/// The profile step 4 would save. <see cref="Profile"/> is null when something is missing (<see cref="Problem"/>);
/// <see cref="Skipped"/> counts ticked screenings without watched seats, which the profile leaves out (desktop app).
/// </summary>
public sealed record ProfileDraft(WatchProfile? Profile, IReadOnlyList<DraftScreening> Screenings, int Skipped, string? Problem);

/// <summary>
/// The site wizard's step 4 and its editing (docs/SAAS.md §5.2): builds the profile the way the desktop app's
/// <c>ProfileSessionViewModel.BuildProfile</c> does, and turns a stored profile back into the wizard's state.
/// Makes no Cinema City requests: a screening's data comes from the shared cache's repertoire or, when editing, from
/// the stored profile; its seats from the hall map in seat_plans.
/// </summary>
public static class WizardProfile
{
    /// <summary>The desktop's default name: "Diuna Warszawa 12.12" or "Diuna 12.12–14.12" (all cities).</summary>
    public static string DefaultName(WizardState state, string? filmName)
    {
        var name = string.IsNullOrWhiteSpace(filmName) ? T("Profil", "Profile") : filmName.Trim();
        if (!state.IsAllCities && Locations.FindCinema(state.City) is { } cinema)
            name += " " + cinema.DisplayCity;
        var dates = state.Dates;
        return dates.Count switch
        {
            0 => name,
            1 => $"{name} {ScreeningText.ShortDate(dates[0])}",
            _ => $"{name} {ScreeningText.ShortDate(dates[0])}–{ScreeningText.ShortDate(dates[^1])}",
        };
    }

    /// <summary>The film's name: from the repertoire, else from the edited profile when it is the same film.</summary>
    public static string FilmName(WizardState state, IReadOnlyList<DayRepertoire> days, WatchProfile? edited) =>
        days.SelectMany(d => d.Films).FirstOrDefault(f => f.Id == state.FilmId)?.Name
        ?? (edited is not null && edited.FilmId == state.FilmId ? edited.FilmName : "");

    /// <summary>
    /// Step 4's profile from the state. <paramref name="maps"/> give the hall maps (the seats are kept as bits of
    /// them), <paramref name="days"/> the cached repertoire and <paramref name="edited"/> the stored profile being
    /// edited (the source of screenings no longer in the cache).
    /// </summary>
    public static ProfileDraft Build(WizardState state, IReadOnlyList<ScreeningResult> maps, IReadOnlyList<DayRepertoire> days,
        WatchProfile? edited, int minimumInterval)
    {
        var listed = days.SelectMany(d => d.Screenings).GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var stored = (edited?.Screenings ?? []).GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var screenings = new List<DraftScreening>();
        var skipped = 0;
        var missing = 0;
        foreach (var id in state.ScreeningIds)
        {
            var code = state.Seats.GetValueOrDefault(id);
            if (string.IsNullOrEmpty(code) && !state.SeedIds.Contains(id))
            {
                skipped++;
                continue;
            }
            var plan = maps.FirstOrDefault(m => m.ScreeningId == id)?.Plan;
            var keys = plan is null ? null : SeatCode.Decode(plan, code);
            var screening = listed.TryGetValue(id, out var fromRepertoire) ? FromRepertoire(fromRepertoire)
                : stored.TryGetValue(id, out var fromProfile) ? Copy(fromProfile) : null;
            if (keys is null || screening is null || state.SeedIds.Contains(id))
            {
                missing++;
                continue;
            }
            if (keys.Count == 0)
            {
                skipped++;
                continue;
            }
            screening.SeatKeys = keys.Order(StringComparer.Ordinal).ToList();
            screenings.Add(new DraftScreening(screening, keys.Count));
        }

        if (missing > 0)
            return new ProfileDraft(null, screenings, skipped, T(
                $"Brakuje danych {Count(missing, "seansu", "seansów", "seansów", "", "")} (minęło sporo czasu albo serwer był restartowany). Wróć do kroku 2 i wczytaj seanse ponownie.",
                $"The data of {Count(missing, "", "", "", "screening", "screenings")} is missing (a while has passed or the server was restarted). Go back to step 2 and load the screenings again."));
        if (screenings.Count == 0)
            return new ProfileDraft(null, screenings, skipped, T("Zaznacz co najmniej jeden rząd albo jedno miejsce.", "Select at least one row or seat."));

        screenings.Sort((a, b) => a.Screening.StartsAt.CompareTo(b.Screening.StartsAt));
        var cinemaIds = state.CinemaIds;
        var dates = state.Dates;
        var filmName = FilmName(state, days, edited);
        var profile = new WatchProfile
        {
            Name = state.Name ?? DefaultName(state, filmName),
            // Version 1.3 replays a profile with CinemaId only, so the cinema of the first watched screening (desktop).
            CinemaId = screenings[0].Screening.CinemaId is { Length: > 0 } first ? first : cinemaIds[0],
            CinemaName = state.IsAllCities
                ? T($"Wszystkie kina IMAX ({Locations.ImaxCinemas.Count})", $"All IMAX cinemas ({Locations.ImaxCinemas.Count})")
                : Locations.FindCinema(state.City)?.Name ?? "",
            CinemaIds = cinemaIds.ToList(),
            FilmId = state.FilmId ?? "",
            FilmName = filmName,
            DateFrom = dates.Count > 0 ? dates[0] : DateOnly.FromDateTime(screenings[0].Screening.StartsAt),
            DateTo = dates.Count > 0 ? dates[^1] : DateOnly.FromDateTime(screenings[^1].Screening.StartsAt),
            MinAdjacent = state.MinAdjacent,
            IntervalMinutes = state.IntervalOrDefault(minimumInterval),
            Screenings = screenings.Select(s => s.Screening).ToList(),
        };
        return new ProfileDraft(profile, screenings, skipped, null);
    }

    /// <summary>
    /// Editing: the wizard's state for a stored profile, as the desktop app replays one - its cinemas, its dates from
    /// today on, its film, its screenings that have not started (<paramref name="dropped"/> counts the others), its
    /// settings. The seats follow through <see cref="Seed"/> once the hall maps are known.
    /// </summary>
    public static WizardState FromProfile(Guid id, string name, WatchProfile profile, DateTime polishNow, out int dropped)
    {
        var today = DateOnly.FromDateTime(polishNow);
        var upcoming = profile.Screenings.Where(s => s.StartsAt > polishNow).Select(s => s.Id)
            .Distinct(StringComparer.Ordinal).Take(WizardState.MaxCarriedScreenings).ToList();
        dropped = profile.Screenings.Count - upcoming.Count;

        var cinemas = (profile.CinemaIds.Count > 0 ? profile.CinemaIds : [profile.CinemaId]).Distinct(StringComparer.Ordinal).ToList();
        var city = cinemas.Count > 1 ? WizardState.AllCities : Locations.FindCinema(cinemas[0])?.Id ?? Locations.Sadyba.Id;
        var from = profile.DateFrom < today ? today : profile.DateFrom;
        return new WizardState
        {
            City = city,
            From = from,
            To = profile.DateTo > from ? profile.DateTo : null,
            FilmId = ProfileImport.IsValidId(profile.FilmId) ? profile.FilmId : null,
            ScreeningIds = upcoming,
            Furthest = WizardState.LastStep,
            MinAdjacent = Math.Clamp(profile.MinAdjacent, 1, WizardState.MaxAdjacent),
            ProfileId = id,
            SeedIds = upcoming,
            Name = name,
            Interval = WizardState.IntervalChoices.Contains(profile.IntervalMinutes) ? profile.IntervalMinutes : null,
        };
    }

    /// <summary>
    /// Editing: the stored profile's seats for the screenings still waiting for them (<see cref="WizardState.SeedIds"/>)
    /// whose hall map is now known; the others keep waiting (a later loading brings their maps).
    /// </summary>
    public static WizardState Seed(WizardState state, WatchProfile edited, IReadOnlyList<ScreeningResult> maps)
    {
        var waiting = new List<string>();
        foreach (var id in state.SeedIds)
        {
            var plan = maps.FirstOrDefault(m => m.ScreeningId == id)?.Plan;
            if (plan is null)
            {
                waiting.Add(id);
                continue;
            }
            var keys = edited.Screenings.FirstOrDefault(s => s.Id == id)?.SeatKeys ?? [];
            state = state.WithSeats(id, SeatCode.Encode(plan, keys.ToHashSet(StringComparer.Ordinal)));
        }
        return state with { SeedIds = waiting };
    }

    private static WatchedScreening FromRepertoire(Screening screening) => new()
    {
        Id = screening.Id,
        CinemaId = screening.CinemaId,
        City = Locations.FindCinema(screening.CinemaId)?.City ?? "",
        StartsAt = screening.StartsAt,
        Auditorium = screening.Auditorium,
        BookingLink = screening.BookingLink,
    };

    private static WatchedScreening Copy(WatchedScreening screening) => new()
    {
        Id = screening.Id,
        CinemaId = screening.CinemaId,
        City = screening.City,
        StartsAt = screening.StartsAt,
        Auditorium = screening.Auditorium,
        BookingLink = screening.BookingLink,
    };
}
