using System.Collections.ObjectModel;
using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>
/// Step 3: one hall map per selected screening (shown as tabs). The user marks rows / seats to watch.
/// Selections are kept per screening id, so going back and forth (which reloads occupancy) keeps them.
/// </summary>
public sealed class SeatsStepViewModel : ObservableObject
{
    private readonly Dictionary<string, HashSet<string>> _selections = new(StringComparer.Ordinal);
    private ScreeningSeatsItem? _selectedItem;
    private int _minAdjacent = 1;
    private Func<string>? _summary;
    private Func<string>? _actionText;

    public SeatsStepViewModel()
    {
        ApplyToAllCommand = new RelayCommand(ApplyToAll, () => SelectedItem?.Map is not null && Items.Count > 1);
        ClearCommand = new RelayCommand(() => SelectedItem?.Map?.Clear(), () => SelectedItem?.Map is not null);
    }

    public ObservableCollection<ScreeningSeatsItem> Items { get; } = [];

    /// <summary>Raised when the watched seats of any screening change.</summary>
    public event EventHandler? SelectionChanged;

    public RelayCommand ApplyToAllCommand { get; }
    public RelayCommand ClearCommand { get; }

    private static readonly IReadOnlyList<int> AdjacentChoices = [1, 2, 3, 4, 5, 6];

    /// <summary>Shared by all tabs (see <see cref="DatesStepViewModel.Cities"/>).</summary>
    public IReadOnlyList<int> AdjacentOptions => AdjacentChoices;

    public string HintText => T(
        "Kliknij numer rzędu, aby zaznaczyć cały rząd. Kliknij miejsce, aby je dodać lub usunąć - także zajęte.",
        "Click a row number to select the whole row. Click a seat to add or remove it - taken seats too.");

    public ScreeningSeatsItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            // Ignore the null WPF pushes while the view switches to another profile tab.
            if (value is null && Items.Count > 0) return;
            if (!SetProperty(ref _selectedItem, value)) return;
            SetActionText(null);
            RefreshCommands();
        }
    }

    /// <summary>Notify only when at least this many watched seats are free next to each other.</summary>
    public int MinAdjacent
    {
        get => _minAdjacent;
        set
        {
            if (!SetProperty(ref _minAdjacent, Math.Max(1, value))) return;
            foreach (var item in Items)
                item.SetMinAdjacent(_minAdjacent);
        }
    }

    /// <summary>Status line after loading (shown in the window footer).</summary>
    public string? Summary => _summary?.Invoke();

    public string? ActionText => _actionText?.Invoke();

    /// <returns>True when every screening loaded without an error.</returns>
    public async Task<bool> LoadAsync(SeatAvailabilityService service, IReadOnlyList<Screening> screenings, CancellationToken cancellationToken)
    {
        Items.Clear();
        SetSummary(null);
        SetActionText(null);
        var showCity = screenings.Select(s => s.CinemaId).Distinct().Count() > 1;
        foreach (var screening in screenings)
            Items.Add(new ScreeningSeatsItem(screening, _minAdjacent, showCity));
        SelectedItem = Items.FirstOrDefault();

        var loaded = 0;
        foreach (var item in Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var seats = await service.GetScreeningSeatsAsync(item.Screening.Id, cancellationToken);
                _selections.TryGetValue(item.Screening.Id, out var previous);
                item.SetResult(seats, previous);
                item.Map!.SelectionChanged += (_, _) => OnSelectionChanged(item);
                loaded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                item.SetError(ex);
            }
            RefreshCommands();
        }

        var total = Items.Count;
        SetSummary(loaded == total
            ? () => T("Pobrano mapy sal: ", "Hall maps loaded: ") + Count(loaded, "seans", "seanse", "seansów", "screening", "screenings") + "."
            : () => T($"Pobrano {loaded} z {total}. Seanse z błędem zostaną pominięte.",
                      $"Loaded {loaded} of {total}. Screenings with errors will be skipped."));
        return loaded == total;
    }

    /// <summary>Preloads watched seats per screening id (used when opening a profile).</summary>
    public void SetSelections(IEnumerable<(string ScreeningId, IEnumerable<string> SeatKeys)> selections)
    {
        _selections.Clear();
        foreach (var (id, keys) in selections)
            _selections[id] = new HashSet<string>(keys, StringComparer.Ordinal);
    }

    public void Reset()
    {
        Items.Clear();
        _selections.Clear();
        SelectedItem = null;
        MinAdjacent = 1;
        SetSummary(null);
        SetActionText(null);
    }

    /// <summary>Returns an error message in the current language, or null when at least one seat is watched.</summary>
    public string? Validate() =>
        Items.Any(i => i.WatchedCount > 0) ? null : T("Zaznacz co najmniej jeden rząd albo jedno miejsce.", "Select at least one row or seat.");

    public void RefreshTexts()
    {
        foreach (var item in Items)
            item.RefreshTexts();
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ActionText));
    }

    private void OnSelectionChanged(ScreeningSeatsItem item)
    {
        if (item.Map is null) return;
        _selections[item.Screening.Id] = new HashSet<string>(item.Map.WatchedKeys, StringComparer.Ordinal);
        item.UpdateSelection();
        SetActionText(null);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyToAll()
    {
        var source = SelectedItem?.Map;
        if (source is null) return;

        var copied = 0;
        var skipped = 0;
        foreach (var item in Items.Where(i => !ReferenceEquals(i, SelectedItem)))
        {
            if (item.Map is not null && item.Map.LayoutId == source.LayoutId)
            {
                item.Map.SetWatched(source.WatchedKeys);
                copied++;
            }
            else
            {
                skipped++;
            }
        }

        SetActionText(copied == 0
            ? () => T("Brak innych seansów z tą samą salą.", "No other screenings in the same hall.")
            : () => T($"Skopiowano zaznaczenie do {copied} {Plural(copied, "seansu", "seansów", "seansów", "", "")}",
                      $"Selection copied to {Count(copied, "", "", "", "screening", "screenings")}")
                    + (skipped > 0
                        ? T($" (pominięto {skipped}: inna sala lub błąd pobierania).", $" ({skipped} skipped: different hall or loading error).")
                        : "."));
    }

    private void SetSummary(Func<string>? text)
    {
        _summary = text;
        OnPropertyChanged(nameof(Summary));
    }

    private void SetActionText(Func<string>? text)
    {
        _actionText = text;
        OnPropertyChanged(nameof(ActionText));
    }

    private void RefreshCommands()
    {
        ApplyToAllCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One screening on step 3 (a tab with its hall map).</summary>
public sealed class ScreeningSeatsItem(Screening screening, int minAdjacent, bool showCity = false) : ObservableObject
{
    private HallMapViewModel? _map;
    private Exception? _error;
    private bool _isLoading = true;
    private int _minAdjacent = minAdjacent;

    public Screening Screening { get; } = screening;
    /// <summary>"sob. 24.10, 19:00", or "Kraków · sob. 24.10, 19:00" when screenings of several cinemas are open.</summary>
    public string Title => showCity && Locations.FindCinema(Screening.CinemaId) is { } cinema
        ? $"{cinema.DisplayCity} · {Formatting.ShortDateTime(Screening.StartsAt)}"
        : Formatting.ShortDateTime(Screening.StartsAt);

    public HallMapViewModel? Map
    {
        get => _map;
        private set
        {
            if (SetProperty(ref _map, value))
                OnPropertyChanged(nameof(HasMap));
        }
    }

    public bool HasMap => Map is not null;
    public ScreeningSeats? Seats => Map?.Seats;
    public int WatchedCount => Map?.WatchedCount ?? 0;

    /// <summary>"Monitorowane: 37" / "Watched: 37"</summary>
    public string WatchedText => $"{T("Monitorowane", "Watched")}: {WatchedCount}";

    /// <summary>Short occupancy text for the tab.</summary>
    public string StatusText => this switch
    {
        { IsLoading: true } => T("Ładowanie…", "Loading…"),
        { IsError: true } => T("Błąd pobierania", "Loading failed"),
        { Seats: { FreeCount: 0 } } => T("Brak wolnych miejsc", "No free seats"),
        { Seats: { } s } => T($"Wolne: {s.FreeCount} z {s.TotalCount}", $"Free: {s.FreeCount} of {s.TotalCount}"),
        _ => "",
    };

    public string? ErrorText => _error is null ? null : ErrorMessages.Describe(_error);
    public bool IsError => _error is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Shown instead of the map while loading or after an error.</summary>
    public string PlaceholderText => ErrorText ?? (IsLoading ? T("Ładowanie mapy sali…", "Loading the hall map…") : "");

    /// <summary>"Monitorujesz 37 miejsc · teraz wolnych: 5"</summary>
    public string SelectionText
    {
        get
        {
            if (Map is null || Map.WatchedCount == 0)
                return T("Nic nie zaznaczono", "Nothing selected");

            var available = SeatMatcher.FindAvailable(Map.Seats, Map.WatchedKeys, _minAdjacent).Count;
            var watched = Map.WatchedCount;
            return _minAdjacent > 1
                ? T($"Monitorujesz {Count(watched, "miejsce", "miejsca", "miejsc", "", "")} · teraz wolnych (min. {_minAdjacent} obok siebie): {available}",
                    $"Watching {Count(watched, "", "", "", "seat", "seats")} · free now ({_minAdjacent}+ together): {available}")
                : T($"Monitorujesz {Count(watched, "miejsce", "miejsca", "miejsc", "", "")} · teraz wolnych: {available}",
                    $"Watching {Count(watched, "", "", "", "seat", "seats")} · free now: {available}");
        }
    }

    public void SetResult(ScreeningSeats seats, IEnumerable<string>? watchedKeys)
    {
        Map = new HallMapViewModel(seats, watchedKeys);
        IsLoading = false;
        RefreshTexts();
    }

    public void SetError(Exception error)
    {
        _error = error;
        IsLoading = false;
        RefreshTexts();
    }

    public void SetMinAdjacent(int minAdjacent)
    {
        _minAdjacent = minAdjacent;
        OnPropertyChanged(nameof(SelectionText));
    }

    public void UpdateSelection()
    {
        OnPropertyChanged(nameof(WatchedCount));
        OnPropertyChanged(nameof(WatchedText));
        OnPropertyChanged(nameof(SelectionText));
    }

    public void RefreshTexts()
    {
        Map?.RefreshTexts();
        foreach (var name in new[]
                 {
                     nameof(Title), nameof(StatusText), nameof(ErrorText), nameof(IsError), nameof(PlaceholderText),
                     nameof(WatchedCount), nameof(WatchedText), nameof(SelectionText),
                 })
            OnPropertyChanged(name);
    }
}
