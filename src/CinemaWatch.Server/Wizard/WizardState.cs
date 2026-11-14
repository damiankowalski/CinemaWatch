using System.Globalization;
using CinemaWatch.Core;
using CinemaWatch.Server.Profiles;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Server.Wizard;

/// <summary>
/// What the site's wizard knows between its steps (docs/SAAS.md §5.2): it travels in the forms (hidden fields) and in
/// the address of the pages that reload themselves, never in the database. Anyone can change it, so reading it drops
/// whatever is not well formed, and every step checks it again.
/// </summary>
public sealed record WizardState
{
    /// <summary>The "all cities" choice (other values are cinema ids from <see cref="Locations.ImaxCinemas"/>).</summary>
    public const string AllCities = "all";

    public const int FirstStep = 1;

    public const int LastStep = 4;

    /// <summary>Screening ids carried at most (a day of six cinemas has fewer IMAX screenings of one film).</summary>
    public const int MaxCarriedScreenings = 200;

    /// <summary>A cinema id or <see cref="AllCities"/>; Sadyba first, as in the desktop app.</summary>
    public string City { get; init; } = Locations.Sadyba.Id;

    /// <summary>The (first) day; null when the field was left empty.</summary>
    public DateOnly? From { get; init; }

    /// <summary>The last day of a range; null for one day.</summary>
    public DateOnly? To { get; init; }

    public string? FilmId { get; init; }

    /// <summary>The ticked screenings (step 2), in the order they were given.</summary>
    public IReadOnlyList<string> ScreeningIds { get; init; } = [];

    /// <summary>The furthest step reached: the step bar lets the user jump back to any step up to it.</summary>
    public int Furthest { get; init; } = FirstStep;

    /// <summary>Step 3: the watched seats of each ticked screening as a <see cref="SeatCode"/> (empty ones left out).</summary>
    public IReadOnlyDictionary<string, string> Seats { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Step 3: the screening whose hall map is shown (null = the first).</summary>
    public string? Tab { get; init; }

    /// <summary>Step 3: notify only when at least this many watched seats are free next to each other (desktop: 1-6).</summary>
    public int MinAdjacent { get; init; } = 1;

    public const int MaxAdjacent = 6;

    /// <summary>Editing: the stored profile the wizard was opened from (null = a new profile).</summary>
    public Guid? ProfileId { get; init; }

    /// <summary>Editing: screenings whose seats still have to come from the stored profile (their hall map was missing).</summary>
    public IReadOnlyList<string> SeedIds { get; init; } = [];

    /// <summary>Step 4: the profile's name as typed (null = not typed yet, the default name is offered).</summary>
    public string? Name { get; init; }

    /// <summary>Step 4: the check interval in minutes (null = the default).</summary>
    public int? Interval { get; init; }

    /// <summary>The desktop app's interval choices (minutes).</summary>
    public static IReadOnlyList<int> IntervalChoices { get; } = [5, 10, 15, 30, 60];

    public const int DefaultInterval = 10;

    /// <summary>The choices an account may use: the desktop's, not below the shortest interval the site allows.</summary>
    public static IReadOnlyList<int> IntervalsFrom(int minimum)
    {
        var allowed = IntervalChoices.Where(m => m >= minimum).ToList();
        return allowed.Count > 0 ? allowed : [minimum];
    }

    /// <summary>The interval to show and save: the chosen one when allowed, else the default (or the next allowed).</summary>
    public int IntervalOrDefault(int minimum)
    {
        var allowed = IntervalsFrom(minimum);
        if (Interval is { } chosen && allowed.Contains(chosen))
            return chosen;
        return allowed.FirstOrDefault(m => m >= DefaultInterval, allowed[^1]);
    }

    public bool IsAllCities => City == AllCities;

    public IReadOnlyList<string> CinemaIds => IsAllCities ? Locations.ImaxCinemas.Select(c => c.Id).ToList() : [City];

    /// <summary>The desktop's ranges: 31 days for one city, 14 for all six.</summary>
    public int MaxDays => IsAllCities ? RepertoireRequest.MaxDaysSeveralCinemas : RepertoireRequest.MaxDaysOneCinema;

    /// <summary>A new wizard: Sadyba, today.</summary>
    public static WizardState New(DateOnly polishToday) => new() { From = polishToday };

    /// <summary>
    /// Reads the state from a form or a query (<paramref name="values"/> answers every value of a name). Unknown
    /// cities fall back to Sadyba, dates must be yyyy-MM-dd, ids follow the import's rule; screening ids may come as
    /// several values (check boxes) or as one comma-separated value (a hidden field, the address).
    /// </summary>
    public static WizardState Read(Func<string, IEnumerable<string?>> values)
    {
        string? One(string name) => values(name).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

        var city = One("city");
        var from = Date(One("from"));
        var to = Date(One("to"));
        var film = One("film");
        var ids = values("s")
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(ProfileImport.IsValidId)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCarriedScreenings)
            .ToList();
        var furthest = int.TryParse(One("max"), NumberStyles.None, CultureInfo.InvariantCulture, out var max) ? max : FirstStep;
        var adjacent = int.TryParse(One("adj"), NumberStyles.None, CultureInfo.InvariantCulture, out var adj) ? adj : 1;
        // "m": "id.code,id.code" - only for ticked screenings.
        var seats = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in values("m").SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var dot = part.IndexOf('.');
            if (dot <= 0)
                continue;
            var (id, code) = (part[..dot], part[(dot + 1)..]);
            if (ids.Contains(id, StringComparer.Ordinal) && SeatCode.IsWellFormed(code))
                seats[id] = code;
        }
        var tab = One("tab");
        // The name as typed, without control characters and cut to the import's limit (an empty one = the default).
        var name = new string((values("name").FirstOrDefault(v => v is not null) ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > ProfileImport.MaxTextLength)
            name = name[..ProfileImport.MaxTextLength].TrimEnd();
        var seed = values("seed")
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(id => ids.Contains(id, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new WizardState
        {
            City = city == AllCities || Locations.FindCinema(city) is not null ? city! : Locations.Sadyba.Id,
            From = from,
            To = to == from ? null : to,
            FilmId = ProfileImport.IsValidId(film) ? film : null,
            ScreeningIds = ids,
            Furthest = Math.Clamp(furthest, FirstStep, LastStep),
            Seats = seats,
            Tab = tab is not null && ids.Contains(tab, StringComparer.Ordinal) ? tab : null,
            MinAdjacent = Math.Clamp(adjacent, 1, MaxAdjacent),
            ProfileId = Guid.TryParse(One("p"), out var profileId) && profileId != Guid.Empty ? profileId : null,
            SeedIds = seed,
            Name = string.IsNullOrEmpty(name) ? null : name,
            Interval = int.TryParse(One("int"), NumberStyles.None, CultureInfo.InvariantCulture, out var interval) && IntervalChoices.Contains(interval) ? interval : null,
        };
    }

    /// <summary>The state as name/value pairs (hidden fields, the address); empty values are left out.</summary>
    public IEnumerable<KeyValuePair<string, string>> Fields(bool withScreenings = true, bool withSeats = true)
    {
        yield return new("city", City);
        if (From is { } from)
            yield return new("from", Text(from));
        if (To is { } to)
            yield return new("to", Text(to));
        if (FilmId is { } film)
            yield return new("film", film);
        if (withScreenings && ScreeningIds.Count > 0)
            yield return new("s", string.Join(",", ScreeningIds));
        yield return new("max", Furthest.ToString(CultureInfo.InvariantCulture));
        if (withSeats && Seats.Count > 0)
            yield return new("m", string.Join(",", ScreeningIds.Where(Seats.ContainsKey).Select(id => $"{id}.{Seats[id]}")));
        if (Tab is { } tab)
            yield return new("tab", tab);
        if (MinAdjacent > 1)
            yield return new("adj", MinAdjacent.ToString(CultureInfo.InvariantCulture));
        if (ProfileId is { } profile)
            yield return new("p", profile.ToString());
        if (SeedIds.Where(ScreeningIds.Contains).ToList() is { Count: > 0 } seed)
            yield return new("seed", string.Join(",", seed));
        if (Name is { } name)
            yield return new("name", name);
        if (Interval is { } interval)
            yield return new("int", interval.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The screening whose map step 3 shows: the chosen tab, else the first ticked screening.</summary>
    public string? CurrentTab => Tab ?? ScreeningIds.FirstOrDefault();

    /// <summary>
    /// The same state with this screening's selection replaced (an empty one is left out); it no longer waits for the
    /// edited profile's seats.
    /// </summary>
    public WizardState WithSeats(string screeningId, string code)
    {
        var seats = new Dictionary<string, string>(Seats, StringComparer.Ordinal);
        if (code.Length == 0)
            seats.Remove(screeningId);
        else
            seats[screeningId] = code;
        return this with { Seats = seats, SeedIds = SeedIds.Where(id => id != screeningId).ToList() };
    }

    /// <summary>"city=1060&amp;from=2026-11-12&amp;max=2" for an address.</summary>
    public string Query() =>
        string.Join("&", Fields().Select(f => $"{f.Key}={Uri.EscapeDataString(f.Value)}"));

    /// <summary>The days of step 1 (empty without a first day; a reversed range gives none).</summary>
    public IReadOnlyList<DateOnly> Dates
    {
        get
        {
            if (From is not { } from)
                return [];
            var to = To ?? from;
            return to < from ? [] : Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).ToList();
        }
    }

    /// <summary>Step 1's check, with the desktop app's messages (current language); null when the input is fine.</summary>
    public string? DatesProblem(DateOnly polishToday)
    {
        if (From is not { } from)
            return T("Wybierz datę seansu.", "Choose a date.");
        if (from < polishToday)
            return T("Data nie może być z przeszłości.", "The date cannot be in the past.");
        if (To is not { } to)
            return null;
        if (to < from)
            return T("Data końcowa nie może być wcześniejsza niż początkowa.", "The end date cannot be before the start date.");
        if (to.DayNumber - from.DayNumber + 1 > MaxDays)
            return RangeHint;
        return null;
    }

    /// <summary>"Zakres może obejmować maksymalnie 31 dni." (14 for all cities), as in the desktop app.</summary>
    public string RangeHint => IsAllCities
        ? T($"Dla wszystkich miast zakres może obejmować maksymalnie {MaxDays} dni.", $"For all cities the range can cover at most {MaxDays} days.")
        : T($"Zakres może obejmować maksymalnie {MaxDays} dni.", $"The range can cover at most {MaxDays} days.");

    /// <summary>Step 2's check; null when the selection is fine.</summary>
    public string? SelectionProblem(int maxScreenings)
    {
        if (ScreeningIds.Count == 0)
            return T("Zaznacz co najmniej jeden seans.", "Select at least one screening.");
        if (ScreeningIds.Count > maxScreenings)
            return T($"Profil może pilnować najwyżej {maxScreenings} seansów (zaznaczone: {ScreeningIds.Count}).",
                     $"A profile can watch at most {maxScreenings} screenings ({ScreeningIds.Count} selected).");
        return null;
    }

    /// <summary>What step 1 asks Cinema City for (check <see cref="DatesProblem"/> first).</summary>
    public RepertoireRequest Repertoire() => new(CinemaIds, From ?? default, To ?? From ?? default);

    public static string Text(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? Date(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
