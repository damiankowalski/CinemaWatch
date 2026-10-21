using System.Collections.ObjectModel;
using CinemaWatch.Core;

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
    private string? _summary;
    private string? _actionText;

    public SeatsStepViewModel()
    {
        ApplyToAllCommand = new RelayCommand(ApplyToAll, () => SelectedItem?.Map is not null && Items.Count > 1);
        ClearCommand = new RelayCommand(() => SelectedItem?.Map?.Clear(), () => SelectedItem?.Map is not null);
    }

    public ObservableCollection<ScreeningSeatsItem> Items { get; } = [];

    public RelayCommand ApplyToAllCommand { get; }
    public RelayCommand ClearCommand { get; }

    public IReadOnlyList<int> AdjacentOptions { get; } = [1, 2, 3, 4, 5, 6];

    public string HintText => "Kliknij numer rzędu, aby zaznaczyć cały rząd. Kliknij miejsce, aby je dodać lub usunąć - także zajęte.";

    public ScreeningSeatsItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            ActionText = null;
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
                item.UpdateSelectionText(_minAdjacent);
        }
    }

    public string? Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string? ActionText
    {
        get => _actionText;
        private set => SetProperty(ref _actionText, value);
    }

    public async Task LoadAsync(SeatAvailabilityService service, IReadOnlyList<Screening> screenings, CancellationToken cancellationToken)
    {
        Items.Clear();
        Summary = null;
        ActionText = null;
        foreach (var screening in screenings)
            Items.Add(new ScreeningSeatsItem(screening));
        SelectedItem = Items.FirstOrDefault();

        var loaded = 0;
        foreach (var item in Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var seats = await service.GetScreeningSeatsAsync(item.Screening.Id, cancellationToken);
                _selections.TryGetValue(item.Screening.Id, out var previous);
                item.SetResult(seats, previous, _minAdjacent);
                item.Map!.SelectionChanged += (_, _) => OnSelectionChanged(item);
                loaded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                item.SetError(ErrorMessages.Describe(ex));
            }
            RefreshCommands();
        }

        Summary = loaded == Items.Count
            ? $"Pobrano mapy sal: {Formatting.Count(loaded, "seans", "seanse", "seansów")}."
            : $"Pobrano {loaded} z {Items.Count}. Seanse z błędem zostaną pominięte.";
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
        Summary = null;
        ActionText = null;
    }

    /// <summary>Returns a Polish error message, or null when at least one seat is watched.</summary>
    public string? Validate() =>
        Items.Any(i => i.WatchedCount > 0) ? null : "Zaznacz co najmniej jeden rząd albo jedno miejsce.";

    private void OnSelectionChanged(ScreeningSeatsItem item)
    {
        if (item.Map is null) return;
        _selections[item.Screening.Id] = new HashSet<string>(item.Map.WatchedKeys, StringComparer.Ordinal);
        item.UpdateSelectionText(_minAdjacent);
        ActionText = null;
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

        ActionText = copied == 0
            ? "Brak innych seansów z tą samą salą."
            : $"Skopiowano zaznaczenie do {Formatting.Count(copied, "seansu", "seansów", "seansów")}"
              + (skipped > 0 ? $" (pominięto {skipped}: inna sala lub błąd pobierania)." : ".");
    }

    private void RefreshCommands()
    {
        ApplyToAllCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One screening on step 3 (a tab with its hall map).</summary>
public sealed class ScreeningSeatsItem(Screening screening) : ObservableObject
{
    private HallMapViewModel? _map;
    private string _statusText = "Ładowanie…";
    private string? _errorText;
    private string _selectionText = "";
    private bool _isLoading = true;

    public Screening Screening { get; } = screening;
    public string Title { get; } = Formatting.ShortDateTime(screening.StartsAt);

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

    /// <summary>Short occupancy text for the tab.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(PlaceholderText));
            }
        }
    }

    public bool IsError => ErrorText is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                OnPropertyChanged(nameof(PlaceholderText));
        }
    }

    /// <summary>Shown instead of the map while loading or after an error.</summary>
    public string PlaceholderText => ErrorText ?? (IsLoading ? "Ładowanie mapy sali…" : "");

    /// <summary>"Monitorujesz 37 miejsc · teraz wolnych: 5"</summary>
    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    public void SetResult(ScreeningSeats seats, IEnumerable<string>? watchedKeys, int minAdjacent)
    {
        Map = new HallMapViewModel(seats, watchedKeys);
        StatusText = seats.FreeCount == 0
            ? "Brak wolnych miejsc"
            : $"Wolne: {seats.FreeCount} z {seats.TotalCount}";
        IsLoading = false;
        UpdateSelectionText(minAdjacent);
    }

    public void SetError(string message)
    {
        StatusText = "Błąd pobierania";
        ErrorText = message;
        IsLoading = false;
    }

    public void UpdateSelectionText(int minAdjacent)
    {
        OnPropertyChanged(nameof(WatchedCount));
        if (Map is null || Map.WatchedCount == 0)
        {
            SelectionText = "Nic nie zaznaczono";
            return;
        }

        var available = SeatMatcher.FindAvailable(Map.Seats, Map.WatchedKeys, minAdjacent).Count;
        var condition = minAdjacent > 1 ? $" (min. {minAdjacent} obok siebie)" : "";
        SelectionText = $"Monitorujesz {Formatting.Count(Map.WatchedCount, "miejsce", "miejsca", "miejsc")} · teraz wolnych{condition}: {available}";
    }
}
