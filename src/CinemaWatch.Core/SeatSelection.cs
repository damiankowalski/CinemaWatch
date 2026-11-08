namespace CinemaWatch.Core;

/// <summary>
/// Selection rules of the hall map, shared by every client. The desktop map uses them directly; the site's
/// <c>seatmap.js</c> mirrors them, and both are tested against <c>tests/Fixtures/seat-selection-cases.json</c>.
/// All coordinates are plan units (seat centres <see cref="Seat.X"/>/<see cref="Seat.Y"/>); a client converts the
/// pointer position to plan units before calling these rules. A selection is a set of seat keys.
/// <list type="bullet">
/// <item>Click on a seat: toggles that one seat (wheelchair spaces too).</item>
/// <item>Row number: toggles every regular seat of the row; all are deselected when every one already was.</item>
/// <item>Painting (press on a regular seat and drag): the first seat decides select or deselect; every regular seat
/// the pointer passes over gets that state. Passing over a seat again changes nothing.</item>
/// <item>Rectangle (press on the background and drag): the regular seats whose centre lies inside are all
/// selected, or all deselected when every one of them already was (the row-number rule).</item>
/// <item>Wheelchair spaces change only by a single click: pressing on one and dragging toggles just that space.</item>
/// </list>
/// </summary>
public static class SeatSelection
{
    /// <summary>
    /// The seat under a point: the nearest seat whose centre is at most the seat radius away; null for the
    /// background (also the gaps between seats). Ties go to the seat listed first in the plan.
    /// </summary>
    public static Seat? SeatAt(SeatPlan plan, PlanPoint point)
    {
        var limit = plan.SeatRadius * plan.SeatRadius;
        Seat? best = null;
        var bestDistance = double.MaxValue;
        foreach (var seat in plan.Seats)
        {
            var dx = seat.X - point.X;
            var dy = seat.Y - point.Y;
            var distance = dx * dx + dy * dy;
            if (distance <= limit && distance < bestDistance)
            {
                best = seat;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>
    /// Regular seats the pointer passes over when it moves straight from <paramref name="from"/> to
    /// <paramref name="to"/>: every seat whose centre is at most the seat radius away from the segment. Pointer
    /// events come at intervals, so a fast move must not skip the seats in between.
    /// </summary>
    public static IEnumerable<Seat> RegularSeatsAlong(SeatPlan plan, PlanPoint from, PlanPoint to)
    {
        var limit = plan.SeatRadius * plan.SeatRadius;
        return plan.Seats.Where(s => !s.IsWheelchair && DistanceToSegmentSquared(new PlanPoint(s.X, s.Y), from, to) <= limit);
    }

    /// <summary>Regular seats whose centre lies inside the rectangle spanned by two corners (edges included).</summary>
    public static IEnumerable<Seat> RegularSeatsInside(SeatPlan plan, PlanPoint corner1, PlanPoint corner2)
    {
        var left = Math.Min(corner1.X, corner2.X);
        var right = Math.Max(corner1.X, corner2.X);
        var top = Math.Min(corner1.Y, corner2.Y);
        var bottom = Math.Max(corner1.Y, corner2.Y);
        return plan.Seats.Where(s => !s.IsWheelchair && s.X >= left && s.X <= right && s.Y >= top && s.Y <= bottom);
    }

    /// <summary>Click on a seat: toggles it.</summary>
    public static IReadOnlySet<string> ToggleSeat(IReadOnlySet<string> selection, Seat seat)
    {
        var result = new HashSet<string>(selection, StringComparer.Ordinal);
        if (!result.Remove(seat.Key))
            result.Add(seat.Key);
        return result;
    }

    /// <summary>
    /// Row number and rectangle: selects every regular seat of <paramref name="seats"/>, or deselects them all
    /// when every one already was. Wheelchair spaces are left as they are; no regular seat means no change.
    /// </summary>
    public static IReadOnlySet<string> ToggleGroup(IReadOnlySet<string> selection, IEnumerable<Seat> seats)
    {
        var regular = seats.Where(s => !s.IsWheelchair).Select(s => s.Key).ToList();
        var result = new HashSet<string>(selection, StringComparer.Ordinal);
        if (regular.Count == 0)
            return result;
        if (regular.All(result.Contains))
            result.ExceptWith(regular);
        else
            result.UnionWith(regular);
        return result;
    }

    /// <summary>Row number: toggles the regular seats of one row (section + row key).</summary>
    public static IReadOnlySet<string> ToggleRow(IReadOnlySet<string> selection, SeatPlan plan, string section, string rowKey) =>
        ToggleGroup(selection, plan.Seats.Where(s => s.Section == section && s.RowKey == rowKey));

    /// <summary>
    /// The result of a whole gesture: <paramref name="path"/> holds the pointer positions from the press
    /// (first point) to the release (last point). One point is a plain click (or nothing on the background).
    /// </summary>
    public static IReadOnlySet<string> ApplyGesture(SeatPlan plan, IReadOnlySet<string> before, IReadOnlyList<PlanPoint> path)
    {
        ArgumentOutOfRangeException.ThrowIfZero(path.Count);
        var gesture = new SeatSelectionGesture(plan, before, path[0]);
        for (var i = 1; i < path.Count; i++)
            gesture.MoveTo(path[i]);
        return gesture.Result;
    }

    private static double DistanceToSegmentSquared(PlanPoint p, PlanPoint a, PlanPoint b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lengthSquared = abx * abx + aby * aby;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lengthSquared, 0, 1);
        var dx = a.X + t * abx - p.X;
        var dy = a.Y + t * aby - p.Y;
        return dx * dx + dy * dy;
    }
}

/// <summary>What a press on the map turned into; decided by the point of the press.</summary>
public enum SeatSelectionGestureKind
{
    /// <summary>Press on a wheelchair space: only that space toggles, dragging changes nothing.</summary>
    Toggle,

    /// <summary>Press on a regular seat: painting (without a move it is a click on that seat).</summary>
    Paint,

    /// <summary>Press on the background: rectangle.</summary>
    Rectangle,
}

/// <summary>
/// One gesture on the hall map from the press to the release. <see cref="Result"/> is always computed from
/// <see cref="Before"/>, so a client shows it live and restores <see cref="Before"/> when the gesture is cancelled
/// (Esc, lost mouse capture). Rules: see <see cref="SeatSelection"/>.
/// </summary>
public sealed class SeatSelectionGesture
{
    private readonly SeatPlan _plan;
    private readonly HashSet<string> _painted = new(StringComparer.Ordinal);
    private readonly bool _paintSelects;
    private IReadOnlySet<string> _result;

    public SeatSelectionGesture(SeatPlan plan, IReadOnlySet<string> before, PlanPoint start)
    {
        _plan = plan;
        Before = new HashSet<string>(before, StringComparer.Ordinal);
        Start = start;
        Current = start;
        StartSeat = SeatSelection.SeatAt(plan, start);

        if (StartSeat is null)
        {
            Kind = SeatSelectionGestureKind.Rectangle;
            _result = SeatSelection.ToggleGroup(Before, SeatSelection.RegularSeatsInside(plan, start, start));
        }
        else if (StartSeat.IsWheelchair)
        {
            Kind = SeatSelectionGestureKind.Toggle;
            _result = SeatSelection.ToggleSeat(Before, StartSeat);
        }
        else
        {
            Kind = SeatSelectionGestureKind.Paint;
            _paintSelects = !Before.Contains(StartSeat.Key);
            _painted.Add(StartSeat.Key);
            _result = PaintResult();
        }
    }

    public SeatSelectionGestureKind Kind { get; }

    /// <summary>The selection before the press; restored when the gesture is cancelled.</summary>
    public IReadOnlySet<string> Before { get; }

    /// <summary>The seat under the press, or null for the background.</summary>
    public Seat? StartSeat { get; }

    public PlanPoint Start { get; }
    public PlanPoint Current { get; private set; }

    /// <summary>The selection if the gesture ended now.</summary>
    public IReadOnlySet<string> Result => _result;

    /// <summary>The pointer moved (button still pressed). Returns true when <see cref="Result"/> changed.</summary>
    public bool MoveTo(PlanPoint point)
    {
        var previous = Current;
        Current = point;
        switch (Kind)
        {
            case SeatSelectionGestureKind.Paint:
                var added = false;
                foreach (var seat in SeatSelection.RegularSeatsAlong(_plan, previous, point))
                    added |= _painted.Add(seat.Key);
                if (!added) return false;
                _result = PaintResult();
                return true;

            case SeatSelectionGestureKind.Rectangle:
                var result = SeatSelection.ToggleGroup(Before, SeatSelection.RegularSeatsInside(_plan, Start, point));
                if (result.SetEquals(_result)) return false;
                _result = result;
                return true;

            default:
                return false;
        }
    }

    private IReadOnlySet<string> PaintResult()
    {
        var result = new HashSet<string>(Before, StringComparer.Ordinal);
        if (_paintSelects)
            result.UnionWith(_painted);
        else
            result.ExceptWith(_painted);
        return result;
    }
}
