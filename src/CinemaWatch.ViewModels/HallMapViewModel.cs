using System.Windows.Input;
using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>
/// Interactive hall map of one screening. Seat positions come straight from the Cinema City seat plan
/// (plan units); the view scales the whole canvas with a Viewbox.
/// Selection model: every seat is watched or not. The rules live in <see cref="SeatSelection"/> (shared with the
/// site): a row number toggles all regular seats of the row; a click toggles one seat; pressing on a seat and
/// dragging paints; pressing on the background and dragging draws a rectangle. The view reports a gesture in map
/// units (<see cref="BeginGesture"/>, <see cref="MoveGesture"/>, <see cref="EndGesture"/>,
/// <see cref="CancelGesture"/>); the seats change live and <see cref="SelectionChanged"/> is raised once, at the end.
/// </summary>
public sealed class HallMapViewModel : ObservableObject
{
    private readonly Dictionary<(string Section, string RowKey), List<SeatViewModel>> _rows = [];
    private readonly Dictionary<(string Section, string RowKey), List<RowLabelViewModel>> _rowLabels = [];
    private readonly HallLayout _layout;
    private HashSet<string> _watched = new(StringComparer.Ordinal);
    private bool _suppressChanges;
    private SeatSelectionGesture? _gesture;

    public HallMapViewModel(ScreeningSeats seats, IEnumerable<string>? watchedKeys = null)
    {
        Seats = seats;
        // The geometry is shared with the site's map (CinemaWatch.Core HallLayout).
        var layout = HallLayout.Of(seats.Plan);
        _layout = layout;
        SeatSize = layout.SeatSize;
        Width = layout.Width;
        Height = layout.Height;
        ScreenLeft = layout.ScreenLeft;
        ScreenTop = layout.ScreenTop;
        ScreenWidth = layout.ScreenWidth;
        var initial = new HashSet<string>(watchedKeys ?? [], StringComparer.Ordinal);

        var seatViews = new List<SeatViewModel>(layout.Seats.Count);
        foreach (var placed in layout.Seats)
        {
            var seat = placed.Seat;
            var view = new SeatViewModel(
                seat,
                left: placed.Left,
                top: placed.Top,
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

        var labels = new List<RowLabelViewModel>(layout.RowLabels.Count);
        var commands = new Dictionary<(string Section, string RowKey), RelayCommand>();
        foreach (var row in layout.RowLabels)
        {
            var key = (row.Section, row.RowKey);
            if (!commands.TryGetValue(key, out var command))
                commands[key] = command = new RelayCommand(() => ToggleRow(key));
            var label = new RowLabelViewModel(row.Label, row.Left, row.Top, SeatSize, command);
            if (!_rowLabels.TryGetValue(key, out var pair))
                _rowLabels[key] = pair = [];
            pair.Add(label);
            labels.Add(label);
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
    public double ScreenBarHeight => HallLayout.ScreenBarHeight;

    public IReadOnlyList<SeatViewModel> SeatViews { get; }
    public IReadOnlyList<RowLabelViewModel> RowLabels { get; }

    public IReadOnlySet<string> WatchedKeys => _watched;
    public int WatchedCount => _watched.Count;

    /// <summary>Identifies the auditorium layout; selections can be copied between maps with the same layout.</summary>
    public (int VenueId, int SeatplanId) LayoutId => (Seats.Presentation.VenueId, Seats.Presentation.SeatplanId);

    public void ToggleRow((string Section, string RowKey) row)
    {
        if (!_rows.ContainsKey(row)) return;
        var result = SeatSelection.ToggleRow(_watched, Seats.Plan, row.Section, row.RowKey);
        Batch(() => Apply(result));
    }

    public void SetWatched(IEnumerable<string> keys)
    {
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        Batch(() => Apply(set));
    }

    // ----- Gestures (mouse): press, drag, release. Points are map units, the coordinates of the drawn canvas. -----

    /// <summary>True between the press and the release (or a cancel).</summary>
    public bool IsGestureActive => _gesture is not null;

    /// <summary>The rectangle of a gesture that started on the background, in map units.</summary>
    public bool IsRectangleVisible => _gesture?.Kind == SeatSelectionGestureKind.Rectangle;
    public double RectangleLeft => _gesture is null ? 0 : Math.Min(_gesture.Start.X, _gesture.Current.X) - _layout.OffsetX;
    public double RectangleTop => _gesture is null ? 0 : Math.Min(_gesture.Start.Y, _gesture.Current.Y) - _layout.OffsetY;
    public double RectangleWidth => _gesture is null ? 0 : Math.Abs(_gesture.Current.X - _gesture.Start.X);
    public double RectangleHeight => _gesture is null ? 0 : Math.Abs(_gesture.Current.Y - _gesture.Start.Y);

    /// <summary>Press: starts a click, painting or a rectangle, depending on what lies under the point.</summary>
    public void BeginGesture(double x, double y)
    {
        CancelGesture();
        _gesture = new SeatSelectionGesture(Seats.Plan, _watched, ToPlan(x, y));
        ApplyLive(_gesture.Result);
        RaiseGestureProperties();
    }

    /// <summary>The pointer moved with the button pressed: the seats follow live.</summary>
    public void MoveGesture(double x, double y)
    {
        if (_gesture is null) return;
        if (_gesture.MoveTo(ToPlan(x, y)))
            ApplyLive(_gesture.Result);
        if (_gesture.Kind == SeatSelectionGestureKind.Rectangle)
            RaiseRectangleProperties();
    }

    /// <summary>Release: the selection stays; <see cref="SelectionChanged"/> is raised when it differs from before the press.</summary>
    public void EndGesture()
    {
        if (_gesture is not { } gesture) return;
        _gesture = null;
        RaiseGestureProperties();
        if (!_watched.SetEquals(gesture.Before))
            RecalculateSelection(raiseEvent: true);
    }

    /// <summary>Esc or lost mouse capture: the selection from before the press comes back; no event.</summary>
    public void CancelGesture()
    {
        if (_gesture is not { } gesture) return;
        _gesture = null;
        ApplyLive(gesture.Before);
        RaiseGestureProperties();
    }

    private PlanPoint ToPlan(double x, double y) => _layout.ToPlan(x, y);

    private void ApplyLive(IReadOnlySet<string> keys)
    {
        _suppressChanges = true;
        try
        {
            Apply(keys);
        }
        finally
        {
            _suppressChanges = false;
        }
        RecalculateSelection(raiseEvent: false);
    }

    private void Apply(IReadOnlySet<string> keys)
    {
        foreach (var seat in SeatViews)
            seat.IsWatched = keys.Contains(seat.Key);
    }

    private void RaiseGestureProperties()
    {
        OnPropertyChanged(nameof(IsGestureActive));
        OnPropertyChanged(nameof(IsRectangleVisible));
        RaiseRectangleProperties();
    }

    private void RaiseRectangleProperties()
    {
        OnPropertyChanged(nameof(RectangleLeft));
        OnPropertyChanged(nameof(RectangleTop));
        OnPropertyChanged(nameof(RectangleWidth));
        OnPropertyChanged(nameof(RectangleHeight));
    }

    public void Clear() => SetWatched([]);

    public void RefreshTexts()
    {
        foreach (var seat in SeatViews) seat.RefreshTexts();
        foreach (var label in RowLabels) label.RefreshTexts();
    }

    private void Batch(Action action)
    {
        // A change from outside (row number, Apply to all, Clear) ends a gesture in progress; its result stays.
        if (_gesture is not null)
        {
            _gesture = null;
            RaiseGestureProperties();
        }
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
