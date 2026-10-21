using System.Collections.ObjectModel;
using System.Windows.Input;
using CinemaWatch.Core;

namespace CinemaWatch.ViewModels;

/// <summary>Step 2: pick a film, then tick the screenings (grouped by day) to work with.</summary>
public sealed class ScreeningsStepViewModel : ObservableObject
{
    private IReadOnlyList<DayRepertoire> _days = [];
    private ScreeningFilter _filter = ScreeningFilter.ImaxWithoutDubbing;
    private DateTime _now;
    private FilmOption? _selectedFilm;

    public ScreeningsStepViewModel()
    {
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        ClearAllCommand = new RelayCommand(() => SetAll(false));
    }

    public ObservableCollection<FilmOption> Films { get; } = [];
    public ObservableCollection<DayGroup> Days { get; } = [];

    public ICommand SelectAllCommand { get; }
    public ICommand ClearAllCommand { get; }

    public FilmOption? SelectedFilm
    {
        get => _selectedFilm;
        set
        {
            if (SetProperty(ref _selectedFilm, value))
                RebuildDays();
        }
    }

    public bool HasFilms => Films.Count > 0;
    public bool HasScreenings => Days.Count > 0;

    public string EmptyText => HasFilms
        ? "Ten film nie ma seansów w wybranym terminie."
        : "W wybranym terminie nie ma seansów IMAX z napisami. Seanse z dubbingiem są pomijane.";

    public int SelectedCount => AllOptions.Count(o => o.IsChecked);
    public int TotalCount => AllOptions.Count();

    public string SelectionSummary => TotalCount == 0 ? "" : $"Zaznaczono {SelectedCount} z {TotalCount}";

    private IEnumerable<ScreeningOption> AllOptions => Days.SelectMany(d => d.Screenings);

    public void Load(IReadOnlyList<DayRepertoire> days, ScreeningFilter filter, DateTime now)
    {
        _days = days;
        _filter = filter;
        _now = now;

        var previousFilmId = _selectedFilm?.FilmId;
        var names = days.SelectMany(d => d.Films)
            .GroupBy(f => f.Id)
            .ToDictionary(g => g.Key, g => g.First().Name);

        var options = Matching(days.SelectMany(d => d.Screenings))
            .GroupBy(s => s.FilmId)
            .Select(g => new FilmOption(g.Key, names.GetValueOrDefault(g.Key, g.Key), g.Count()))
            .OrderBy(f => f.Name, StringComparer.Create(Formatting.Polish, ignoreCase: true))
            .ToList();

        _selectedFilm = null;
        Films.Clear();
        foreach (var option in options)
            Films.Add(option);

        _selectedFilm = Films.FirstOrDefault(f => f.FilmId == previousFilmId) ?? Films.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedFilm));
        OnPropertyChanged(nameof(HasFilms));
        RebuildDays();
    }

    /// <summary>Selects a film and ticks the given screenings (used when opening a profile). Returns how many were not found.</summary>
    public int SelectScreenings(string filmId, IEnumerable<string> screeningIds)
    {
        var ids = screeningIds.ToHashSet(StringComparer.Ordinal);
        var film = Films.FirstOrDefault(f => f.FilmId == filmId);
        if (film is null) return ids.Count;
        SelectedFilm = film;
        foreach (var option in AllOptions)
            option.IsChecked = ids.Contains(option.Screening.Id);
        return ids.Count(id => !AllOptions.Any(o => o.Screening.Id == id));
    }

    public IReadOnlyList<Screening> GetSelectedScreenings() =>
        AllOptions.Where(o => o.IsChecked).Select(o => o.Screening).OrderBy(s => s.StartsAt).ToList();

    private IEnumerable<Screening> Matching(IEnumerable<Screening> screenings) =>
        screenings.Where(s => _filter.Matches(s) && s.StartsAt > _now);

    private void RebuildDays()
    {
        Days.Clear();
        if (_selectedFilm is not null)
        {
            foreach (var day in _days.OrderBy(d => d.Date))
            {
                var options = Matching(day.Screenings)
                    .Where(s => s.FilmId == _selectedFilm.FilmId)
                    .OrderBy(s => s.StartsAt)
                    .Select(s => new ScreeningOption(s, OnSelectionChanged))
                    .ToList();
                if (options.Count > 0)
                    Days.Add(new DayGroup(day.Date, options));
            }
        }
        OnPropertyChanged(nameof(HasScreenings));
        OnPropertyChanged(nameof(EmptyText));
        OnSelectionChanged();
    }

    private void SetAll(bool isChecked)
    {
        foreach (var option in AllOptions)
            option.IsChecked = isChecked;
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }
}

public sealed record FilmOption(string FilmId, string Name, int ScreeningCount)
{
    public string DisplayName => $"{Name} ({Formatting.Count(ScreeningCount, "seans", "seanse", "seansów")})";
}

public sealed class DayGroup(DateOnly date, IReadOnlyList<ScreeningOption> screenings)
{
    public DateOnly Date { get; } = date;
    public string Header { get; } = Formatting.DayHeader(date);
    public IReadOnlyList<ScreeningOption> Screenings { get; } = screenings;
}

public sealed class ScreeningOption(Screening screening, Action onChanged) : ObservableObject
{
    private bool _isChecked;

    public Screening Screening { get; } = screening;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                onChanged();
        }
    }

    public string TimeText => Formatting.Time(Screening.StartsAt);
    public string DetailText => Formatting.Languages(Screening);
    public string AvailabilityText => Formatting.Availability(Screening);
    public bool IsNearlySoldOut => Formatting.IsNearlySoldOut(Screening);
}
