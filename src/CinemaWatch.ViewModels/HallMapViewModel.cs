using System.Windows.Input;
using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>
/// Interactive hall map of one screening. Seat positions come straight from the Cinema City seat plan
/// (plan units); the view scales the whole canvas with a Viewbox.
/// Selection model: every seat is watched or not. Clicking a row number toggles all regular seats of
/// the row (wheelchair spaces are only toggled individually).
/// </summary>
public sealed class HallMapViewModel : ObservableObject
{
    private const double Margin = 24;
    private const double ScreenBarUnits = 18;
    private const double ScreenBlockHeight = 64;
    private const double ScreenGap = 40;

    private readonly Dictionary<(string Section, string RowKey), List<SeatViewModel>> _rows = [];
    private readonly Dictionary<(string Section, string RowKey), List<RowLabelViewModel>> _rowLabels = [];
    private HashSet<string> _watched = new(StringComparer.Ordinal);
    private bool _suppressChanges;

    public HallMapViewModel(ScreeningSeats seats, IEnumerable<string>? watchedKeys = null)
    {
        Seats = seats;
        var plan = seats.Plan;
        var radius = plan.SeatRadius;
        SeatSize = radius * 2;
        var initial = new HashSet<string>(watchedKeys ?? [], StringComparer.Ordinal);

        // Row numbers stand in two straight columns at the left and right edge of the hall, like on the
        // Cinema City website. (The plan's own label anchors hug each row, which looks ragged in halls with
        // rows of different widths - e.g. Wrocław, where the last row is a single wheelchair space.)
        var leftColumn = plan.Seats.Min(s => s.X) - SeatSize;
        var rightColumn = plan.Seats.Max(s => s.X) + SeatSize;
        var rowInfos = plan.Seats
            .GroupBy(s => (s.Section, s.RowKey))
            .Select(g =>
            {
                var y = g.Average(s => s.Y);
                return (Key: g.Key, Label: g.First().Row, Left: new PlanPoint(leftColumn, y), Right: new PlanPoint(rightColumn, y));
            })
            .ToList();

        // Bounds of everything drawn on the map.
        var xs = plan.Seats.Select(s => s.X).Concat(rowInfos.SelectMany(r => new[] { r.Left.X, r.Right.X })).ToList();
        var ys = plan.Seats.Select(s => s.Y).Concat(rowInfos.SelectMany(r => new[] { r.Left.Y, r.Right.Y })).ToList();
        var minX = xs.Min() - radius - Margin;
        var maxX = xs.Max() + radius + Margin;
        var minY = ys.Min() - radius - Margin - ScreenGap - ScreenBlockHeight;
        var maxY = ys.Max() + radius + Margin;

        Width = maxX - minX;
        Height = maxY - minY;

        var seatMinX = plan.Seats.Min(s => s.X);
        var seatMaxX = plan.Seats.Max(s => s.X);
        ScreenWidth = Math.Max(SeatSize * 6, (seatMaxX - seatMinX) * 0.7);
        ScreenLeft = (seatMinX + seatMaxX) / 2 - minX - ScreenWidth / 2;
        ScreenTop = Margin;

        var seatViews = new List<SeatViewModel>(plan.Seats.Count);
        foreach (var seat in plan.Seats)
        {
            var view = new SeatViewModel(
                seat,
                left: seat.X - radius - minX,
                top: seat.Y - radius - minY,
                size: SeatSize,
                isFree: seats.IsFree(seat),
                isWatched: initial.Contains(seat.Key),
                onWatchedChanged: OnSeatWatchedChanged);
            seatViews.Add(view);

            var key = (seat.Section, seat.RowKey);
            if (!_rows.TryGetValue(key, out var list))
                _rows[key] = list = [];
            list.Add(view);
        }
        SeatViews = seatViews;

        var labels = new List<RowLabelViewModel>(rowInfos.Count * 2);
        foreach (var row in rowInfos)
        {
            var key = row.Key;
            var command = new RelayCommand(() => ToggleRow(key));
            var pair = new List<RowLabelViewModel>
            {
                new(row.Label, row.Left.X - radius - minX, row.Left.Y - radius - minY, SeatSize, command),
                new(row.Label, row.Right.X - radius - minX, row.Right.Y - radius - minY, SeatSize, command),
            };
            _rowLabels[key] = pair;
            labels.AddRange(pair);
        }
        RowLabels = labels;

        RecalculateSelection(raiseEvent: false);
    }

    /// <summary>Raised after the set of watched seats changed (once per user action).</summary>
    public event EventHandler? SelectionChanged;

    public ScreeningSeats Seats { get; }

    public double Width { get; }
    public double Height { get; }
    public double SeatSize { get; }
    public double ScreenLeft { get; }
    public double ScreenTop { get; }
    public double ScreenWidth { get; }
    public double ScreenBarHeight => ScreenBarUnits;

    public IReadOnlyList<SeatViewModel> SeatViews { get; }
    public IReadOnlyList<RowLabelViewModel> RowLabels { get; }

    public IReadOnlySet<string> WatchedKeys => _watched;
    public int WatchedCount => _watched.Count;

    /// <summary>Identifies the auditorium layout; selections can be copied between maps with the same layout.</summary>
    public (int VenueId, int SeatplanId) LayoutId => (Seats.Presentation.VenueId, Seats.Presentation.SeatplanId);

    public void ToggleRow((string Section, string RowKey) row)
    {
        if (!_rows.TryGetValue(row, out var seats)) return;
        var regular = seats.Where(s => !s.IsWheelchair).ToList();
        var select = !regular.All(s => s.IsWatched);
        Batch(() =>
        {
            foreach (var seat in regular)
                seat.IsWatched = select;
        });
    }

    public void SetWatched(IEnumerable<string> keys)
    {
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        Batch(() =>
        {
            foreach (var seat in SeatViews)
                seat.IsWatched = set.Contains(seat.Key);
        });
    }

    public void Clear() => SetWatched([]);

    public void RefreshTexts()
    {
        foreach (var seat in SeatViews) seat.RefreshTexts();
        foreach (var label in RowLabels) label.RefreshTexts();
    }

    private void Batch(Action action)
    {
        _suppressChanges = true;
        try
        {
            action();
        }
        finally
        {
            _suppressChanges = false;
        }
        RecalculateSelection(raiseEvent: true);
    }

    private void OnSeatWatchedChanged(SeatViewModel seat)
    {
        if (!_suppressChanges)
            RecalculateSelection(raiseEvent: true);
    }

    private void RecalculateSelection(bool raiseEvent)
    {
        _watched = SeatViews.Where(s => s.IsWatched).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, seats) in _rows)
        {
            var regular = seats.Where(s => !s.IsWheelchair).ToList();
            var watched = regular.Count(s => s.IsWatched);
            var state = watched == 0 ? RowWatchState.None
                : watched == regular.Count ? RowWatchState.Full
                : RowWatchState.Partial;
            foreach (var label in _rowLabels[key])
                label.State = state;
        }

        OnPropertyChanged(nameof(WatchedKeys));
        OnPropertyChanged(nameof(WatchedCount));
        if (raiseEvent)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}

public enum RowWatchState
{
    None,
    Partial,
    Full,
}

public sealed class SeatViewModel : ObservableObject
{
    private readonly Action<SeatViewModel> _onWatchedChanged;
    private bool _isWatched;

    public SeatViewModel(Seat seat, double left, double top, double size, bool isFree, bool isWatched, Action<SeatViewModel> onWatchedChanged)
    {
        Seat = seat;
        Left = left;
        Top = top;
        Size = size;
        IsFree = isFree;
        _isWatched = isWatched;
        _onWatchedChanged = onWatchedChanged;
    }

    public Seat Seat { get; }
    public string Key => Seat.Key;
    public double Left { get; }
    public double Top { get; }
    public double Size { get; }
    public bool IsFree { get; }
    public bool IsWheelchair => Seat.IsWheelchair;

    /// <summary>Seat number, or the wheelchair symbol for wheelchair spaces.</summary>
    public string Text => IsWheelchair ? "\u267F" : Seat.Label;

    public string ToolTip =>
        T($"Rząd {Seat.Row}, miejsce {Seat.Label} · {(IsFree ? "wolne" : "zajęte")}",
          $"Row {Seat.Row}, seat {Seat.Label} · {(IsFree ? "free" : "taken")}")
        + (IsWheelchair ? T(" · dla osoby na wózku", " · wheelchair space") : "");

    public void RefreshTexts() => OnPropertyChanged(nameof(ToolTip));

    public bool IsWatched
    {
        get => _isWatched;
        set
        {
            if (SetProperty(ref _isWatched, value))
                _onWatchedChanged(this);
        }
    }
}

public sealed class RowLabelViewModel(string label, double left, double top, double size, ICommand toggleCommand) : ObservableObject
{
    private RowWatchState _state;

    public string Label { get; } = label;
    public double Left { get; } = left;
    public double Top { get; } = top;
    public double Size { get; } = size;
    public ICommand ToggleCommand { get; } = toggleCommand;

    public string ToolTip => T($"Rząd {Label}: kliknij, aby zaznaczyć lub odznaczyć cały rząd",
        $"Row {Label}: click to select or clear the whole row");

    public void RefreshTexts() => OnPropertyChanged(nameof(ToolTip));

    public RowWatchState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsFull));
                OnPropertyChanged(nameof(IsPartial));
            }
        }
    }

    public bool IsFull => State == RowWatchState.Full;
    public bool IsPartial => State == RowWatchState.Partial;
}
