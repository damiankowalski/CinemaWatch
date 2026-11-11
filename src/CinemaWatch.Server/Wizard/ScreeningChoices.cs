using CinemaWatch.Core;

namespace CinemaWatch.Server.Wizard;

/// <summary>A film of step 2 with the number of its screenings that pass the filter.</summary>
public sealed record FilmChoice(string FilmId, string Name, int ScreeningCount);

/// <summary>One cinema's screenings of the chosen film on one day (the cinema is null for an id not on the IMAX list).</summary>
public sealed record CinemaScreenings(Cinema? Cinema, IReadOnlyList<Screening> Screenings);

/// <summary>One day of step 2: the cinemas in the city order of the IMAX list.</summary>
public sealed record DayScreenings(DateOnly Date, IReadOnlyList<CinemaScreenings> Cinemas);

/// <summary>
/// Step 2 of the wizard, as the desktop app builds it (ScreeningsStepViewModel): the films that have screenings passing
/// the filter and not started yet, sorted by name; the chosen film (the previous one when it is still there, otherwise
/// the first); its screenings grouped by day and cinema. Cinema headers only when the repertoire covers several cinemas.
/// </summary>
public sealed record ScreeningChoices(
    IReadOnlyList<FilmChoice> Films,
    FilmChoice? Film,
    IReadOnlyList<DayScreenings> Days,
    bool ShowCinemas)
{
    /// <summary>Every screening listed for the chosen film.</summary>
    public IEnumerable<Screening> Screenings => Days.SelectMany(d => d.Cinemas).SelectMany(c => c.Screenings);

    public static ScreeningChoices Build(IReadOnlyList<DayRepertoire> days, ScreeningFilter filter, DateTime polishNow, string? filmId)
    {
        bool Matching(Screening s) => filter.Matches(s) && s.StartsAt > polishNow;

        var names = days.SelectMany(d => d.Films)
            .GroupBy(f => f.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var films = days.SelectMany(d => d.Screenings).Where(Matching)
            .GroupBy(s => s.FilmId, StringComparer.Ordinal)
            .Select(g => new FilmChoice(g.Key, names.GetValueOrDefault(g.Key, g.Key), g.Count()))
            .OrderBy(f => f.Name, StringComparer.Create(Localizer.Culture, ignoreCase: true))
            .ToList();
        var film = films.FirstOrDefault(f => f.FilmId == filmId) ?? films.FirstOrDefault();

        var grouped = new List<DayScreenings>();
        if (film is not null)
        {
            foreach (var date in days.Select(d => d.Date).Distinct().Order())
            {
                var cinemas = days.Where(d => d.Date == date)
                    .SelectMany(d => d.Screenings.Where(s => Matching(s) && s.FilmId == film.FilmId))
                    .GroupBy(s => s.CinemaId, StringComparer.Ordinal)
                    .OrderBy(g => CinemaOrder(g.Key))
                    .Select(g => new CinemaScreenings(Locations.FindCinema(g.Key), g.OrderBy(s => s.StartsAt).ToList()))
                    .ToList();
                if (cinemas.Count > 0)
                    grouped.Add(new DayScreenings(date, cinemas));
            }
        }
        var showCinemas = days.Select(d => d.Screenings.FirstOrDefault()?.CinemaId).Where(id => id is not null).Distinct().Count() > 1;
        return new ScreeningChoices(films, film, grouped, showCinemas);
    }

    private static int CinemaOrder(string cinemaId)
    {
        for (var i = 0; i < Locations.ImaxCinemas.Count; i++)
            if (Locations.ImaxCinemas[i].Id == cinemaId)
                return i;
        return int.MaxValue;
    }
}

/// <summary>Step 1's repertoire as far as the shared cache holds it (no request).</summary>
public sealed record CachedRepertoire(IReadOnlyList<DayRepertoire> Days, IReadOnlyList<WizardItem> Missing)
{
    public bool Complete => Missing.Count == 0;

    public static CachedRepertoire Read(CinemaCityCache cache, RepertoireRequest request)
    {
        var days = new List<DayRepertoire>();
        var missing = new List<WizardItem>();
        foreach (var item in WizardJobs.Items(request))
        {
            if (cache.TryPeek<DayRepertoire>(item.Key, out var day))
                days.Add(day);
            else
                missing.Add(item);
        }
        return new CachedRepertoire(days, missing);
    }
}
