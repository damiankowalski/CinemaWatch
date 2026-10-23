using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>Step 1: city (one IMAX cinema per city, or all of them) and a single date or a date range.</summary>
public sealed class DatesStepViewModel : ObservableObject
{
    /// <summary>Key of the "all cities" choice (other keys are cinema ids).</summary>
    public const string AllCinemasKey = "all";

    public const int MaxRangeDaysOneCinema = 31;

    /// <summary>All six cinemas cost six repertoire requests per day, hence the shorter range.</summary>
    public const int MaxRangeDaysAllCinemas = 14;

    // The lists are shared by all profile tabs: the view of step 1 is reused when switching tabs, and a
    // different list instance per tab would make WPF drop the shown selection.
    private static readonly IReadOnlyList<ChoiceOption> CityOptions =
        Locations.ImaxCinemas
            .Select(c => new ChoiceOption(c.Id, c.City, c.CityEnglish ?? c.City))
            .Append(new ChoiceOption(AllCinemasKey, "Wszystkie miasta", "All cities"))
            .ToList();

    private static readonly IReadOnlyList<ChoiceOption> CinemaOptions =
        Locations.ImaxCinemas
            .Select(c => new ChoiceOption(c.Id, c.Name, c.Name))
            .Append(new ChoiceOption(AllCinemasKey,
                $"Wszystkie kina IMAX ({Locations.ImaxCinemas.Count})", $"All IMAX cinemas ({Locations.ImaxCinemas.Count})"))
            .ToList();

    private static readonly IReadOnlyList<ChoiceOption> TechnologyOptions =
        [new ChoiceOption(Locations.ImaxTechnology, Locations.ImaxTechnology, Locations.ImaxTechnology)];

    private ChoiceOption _selectedCity;
    private DateTime? _dateFrom = DateTime.Today;
    private DateTime? _dateTo;
    private bool _isRange;

    /// <param name="cinemaKey">Initially selected cinema id or <see cref="AllCinemasKey"/> (e.g. the last choice).</param>
    public DatesStepViewModel(string? cinemaKey = null)
    {
        _selectedCity = FindOption(cinemaKey) ?? FindOption(Locations.Sadyba.Id)!;
    }

    /// <summary>Raised when the user picks another city.</summary>
    public event EventHandler? CinemaSelectionChanged;

    public IReadOnlyList<ChoiceOption> Cities => CityOptions;
    public IReadOnlyList<ChoiceOption> Cinemas => CinemaOptions;
    public IReadOnlyList<ChoiceOption> Technologies => TechnologyOptions;

    public ChoiceOption SelectedCity
    {
        get => _selectedCity;
        set
        {
            // WPF pushes null while the view switches to another profile tab - ignore it.
            if (value is null || !SetProperty(ref _selectedCity, value)) return;
            OnPropertyChanged(nameof(SelectedCinema));
            OnPropertyChanged(nameof(IsAllCinemas));
            OnPropertyChanged(nameof(RangeHint));
            CinemaSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The cinema shown for the chosen city ("All IMAX cinemas" for all cities).</summary>
    public ChoiceOption SelectedCinema => CinemaOptions.First(o => o.Key == _selectedCity.Key);

    public ChoiceOption SelectedTechnology => TechnologyOptions[0];

    public bool IsAllCinemas => _selectedCity.Key == AllCinemasKey;

    /// <summary>Cinema id or <see cref="AllCinemasKey"/>.</summary>
    public string CinemaKey => _selectedCity.Key;

    /// <summary>Cinemas whose repertoire is searched.</summary>
    public IReadOnlyList<Cinema> SelectedCinemas => IsAllCinemas
        ? Locations.ImaxCinemas
        : [Locations.FindCinema(_selectedCity.Key) ?? Locations.Sadyba];

    public DateTime? DateFrom
    {
        get => _dateFrom;
        set
        {
            if (!SetProperty(ref _dateFrom, value)) return;
            if (_isRange && value is not null && (_dateTo is null || _dateTo < value))
                DateTo = value;
        }
    }

    public DateTime? DateTo
    {
        get => _dateTo;
        set => SetProperty(ref _dateTo, value);
    }

    public bool IsRange
    {
        get => _isRange;
        set
        {
            if (!SetProperty(ref _isRange, value)) return;
            OnPropertyChanged(nameof(DateFromLabel));
            if (value && (_dateTo is null || _dateTo < _dateFrom))
                DateTo = _dateFrom;
        }
    }

    public string DateFromLabel => IsRange ? T("Od", "From") : T("Data", "Date");

    public int MaxRangeDays => IsAllCinemas ? MaxRangeDaysAllCinemas : MaxRangeDaysOneCinema;

    public string RangeHint => IsAllCinemas
        ? T($"Dla wszystkich miast zakres może obejmować maksymalnie {MaxRangeDays} dni.",
            $"For all cities the range can cover at most {MaxRangeDays} days.")
        : T($"Zakres może obejmować maksymalnie {MaxRangeDays} dni.", $"The range can cover at most {MaxRangeDays} days.");

    /// <summary>Selects a cinema (or all) by key, e.g. when a profile is opened. Unknown keys are ignored.</summary>
    public void SelectCinema(string key)
    {
        if (FindOption(key) is { } option)
            SelectedCity = option;
    }

    public void Reset()
    {
        IsRange = false;
        DateTo = null;
        DateFrom = DateTime.Today;
    }

    /// <summary>Returns an error message in the current language, or null when the input is valid.</summary>
    public string? Validate(DateTime today)
    {
        if (DateFrom is null)
            return T("Wybierz datę seansu.", "Choose a date.");
        if (DateFrom.Value.Date < today.Date)
            return T("Data nie może być z przeszłości.", "The date cannot be in the past.");
        if (!IsRange)
            return null;
        if (DateTo is null)
            return T("Wybierz datę końcową zakresu.", "Choose the end date of the range.");
        if (DateTo.Value.Date < DateFrom.Value.Date)
            return T("Data końcowa nie może być wcześniejsza niż początkowa.", "The end date cannot be before the start date.");
        if ((DateTo.Value.Date - DateFrom.Value.Date).Days + 1 > MaxRangeDays)
            return RangeHint;
        return null;
    }

    public IReadOnlyList<DateOnly> GetDates()
    {
        if (DateFrom is null) return [];
        var from = DateOnly.FromDateTime(DateFrom.Value);
        var to = IsRange && DateTo is not null ? DateOnly.FromDateTime(DateTo.Value) : from;
        var dates = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
            dates.Add(d);
        return dates;
    }

    public void RefreshTexts()
    {
        foreach (var option in Cities.Concat(Cinemas).Concat(Technologies))
            option.RefreshTexts();
        OnPropertyChanged(nameof(DateFromLabel));
        OnPropertyChanged(nameof(RangeHint));
    }

    private static ChoiceOption? FindOption(string? key) => CityOptions.FirstOrDefault(o => o.Key == key);
}

/// <summary>An option in a drop-down whose display name depends on the UI language.</summary>
public sealed class ChoiceOption(string key, string polishName, string englishName) : ObservableObject
{
    public string Key { get; } = key;
    public string Name => T(polishName, englishName);

    public void RefreshTexts() => OnPropertyChanged(nameof(Name));
}
