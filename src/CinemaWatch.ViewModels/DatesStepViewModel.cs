using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>Step 1: cinema (fixed for now) and a single date or a date range.</summary>
public sealed class DatesStepViewModel : ObservableObject
{
    public const int MaxRangeDays = 31;

    private DateTime? _dateFrom = DateTime.Today;
    private DateTime? _dateTo;
    private bool _isRange;

    public DatesStepViewModel()
    {
        Cities = [new ChoiceOption("Warszawa", "Warsaw")];
        Cinemas = [new ChoiceOption(Locations.Sadyba.Name, Locations.Sadyba.Name)];
        Technologies = [new ChoiceOption(Locations.ImaxTechnology, Locations.ImaxTechnology)];
    }

    // Fixed for now; the lists are ready for city / cinema / technology selection later.
    public IReadOnlyList<ChoiceOption> Cities { get; }
    public IReadOnlyList<ChoiceOption> Cinemas { get; }
    public IReadOnlyList<ChoiceOption> Technologies { get; }

    public Cinema Cinema => Locations.Sadyba;

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

    public string RangeHint => T($"Zakres może obejmować maksymalnie {MaxRangeDays} dni.", $"The range can cover at most {MaxRangeDays} days.");

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
}

/// <summary>An option in a drop-down whose display name depends on the UI language.</summary>
public sealed class ChoiceOption(string polishName, string englishName) : ObservableObject
{
    public string Name => T(polishName, englishName);

    public void RefreshTexts() => OnPropertyChanged(nameof(Name));
}
