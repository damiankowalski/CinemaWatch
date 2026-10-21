using CinemaWatch.Core;

namespace CinemaWatch.ViewModels;

/// <summary>Step 1: cinema (fixed for now) and a single date or a date range.</summary>
public sealed class DatesStepViewModel : ObservableObject
{
    public const int MaxRangeDays = 31;

    private DateTime? _dateFrom = DateTime.Today;
    private DateTime? _dateTo;
    private bool _isRange;

    // Fixed for now; the lists are ready for city / cinema / technology selection later.
    public IReadOnlyList<string> Cities { get; } = [Locations.Sadyba.City];
    public IReadOnlyList<string> Cinemas { get; } = [Locations.Sadyba.Name];
    public IReadOnlyList<string> Technologies { get; } = [Locations.ImaxTechnology];

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

    public string DateFromLabel => IsRange ? "Od" : "Data";

    public string RangeHint => $"Zakres może obejmować maksymalnie {MaxRangeDays} dni.";

    public void Reset()
    {
        IsRange = false;
        DateTo = null;
        DateFrom = DateTime.Today;
    }

    /// <summary>Returns a Polish error message, or null when the input is valid.</summary>
    public string? Validate(DateTime today)
    {
        if (DateFrom is null)
            return "Wybierz datę seansu.";
        if (DateFrom.Value.Date < today.Date)
            return "Data nie może być z przeszłości.";
        if (!IsRange)
            return null;
        if (DateTo is null)
            return "Wybierz datę końcową zakresu.";
        if (DateTo.Value.Date < DateFrom.Value.Date)
            return "Data końcowa nie może być wcześniejsza niż początkowa.";
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
}
