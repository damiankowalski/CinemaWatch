namespace CinemaWatch.Core;

/// <summary>A seat where the map draws it: the top-left corner of its circle, in map units.</summary>
public sealed record PlacedSeat(Seat Seat, double Left, double Top);

/// <summary>A row number at one edge of the hall (every row has two: left and right), in map units.</summary>
public sealed record PlacedRowLabel(string Section, string RowKey, string Label, double Left, double Top);

/// <summary>
/// The geometry of a hall map, shared by the desktop map and the site's map so both look the same: seats at the plan's
/// centres (plan units, circles of <see cref="SeatPlan.SeatRadius"/>), the row numbers in two straight columns at the
/// left and right edge of the hall (like the Cinema City website - the plan's own label anchors hug each row, which
/// looks ragged in halls with rows of different widths, e.g. Wrocław, where the last row is a single wheelchair
/// space), the screen bar above. Map units are plan units moved so that the map starts at 0,0.
/// </summary>
public sealed class HallLayout
{
    public const double Margin = 24;
    public const double ScreenBarHeight = 18;
    public const double ScreenBlockHeight = 64;
    public const double ScreenGap = 40;

    private HallLayout(SeatPlan plan)
    {
        var radius = plan.SeatRadius;
        SeatSize = radius * 2;

        var leftColumn = plan.Seats.Min(s => s.X) - SeatSize;
        var rightColumn = plan.Seats.Max(s => s.X) + SeatSize;
        var rows = plan.Seats
            .GroupBy(s => (s.Section, s.RowKey))
            .Select(g => (g.Key.Section, g.Key.RowKey, Label: g.First().Row, Y: g.Average(s => s.Y)))
            .ToList();

        // Bounds of everything drawn on the map.
        var xs = plan.Seats.Select(s => s.X).Concat([leftColumn, rightColumn]).ToList();
        var ys = plan.Seats.Select(s => s.Y).ToList();
        var minX = xs.Min() - radius - Margin;
        var maxX = xs.Max() + radius + Margin;
        var minY = ys.Min() - radius - Margin - ScreenGap - ScreenBlockHeight;
        var maxY = ys.Max() + radius + Margin;

        Width = maxX - minX;
        Height = maxY - minY;
        OffsetX = minX;
        OffsetY = minY;

        var seatMinX = plan.Seats.Min(s => s.X);
        var seatMaxX = plan.Seats.Max(s => s.X);
        ScreenWidth = Math.Max(SeatSize * 6, (seatMaxX - seatMinX) * 0.7);
        ScreenLeft = (seatMinX + seatMaxX) / 2 - minX - ScreenWidth / 2;
        ScreenTop = Margin;

        Seats = plan.Seats.Select(s => new PlacedSeat(s, s.X - radius - minX, s.Y - radius - minY)).ToList();
        RowLabels = rows.SelectMany(r => new[]
        {
            new PlacedRowLabel(r.Section, r.RowKey, r.Label, leftColumn - radius - minX, r.Y - radius - minY),
            new PlacedRowLabel(r.Section, r.RowKey, r.Label, rightColumn - radius - minX, r.Y - radius - minY),
        }).ToList();
    }

    public double Width { get; }
    public double Height { get; }

    /// <summary>The diameter of a seat (and of a row number's circle).</summary>
    public double SeatSize { get; }

    public double ScreenLeft { get; }
    public double ScreenTop { get; }
    public double ScreenWidth { get; }

    /// <summary>Plan coordinates of the map's 0,0 (a map point plus these is a plan point).</summary>
    public double OffsetX { get; }
    public double OffsetY { get; }

    /// <summary>Every seat, in the plan's order.</summary>
    public IReadOnlyList<PlacedSeat> Seats { get; }

    /// <summary>Two per row (left, right), in the order the rows first appear in the plan.</summary>
    public IReadOnlyList<PlacedRowLabel> RowLabels { get; }

    public static HallLayout Of(SeatPlan plan) => new(plan);

    /// <summary>A point of the drawn map (map units) as a point of the plan, for hit testing.</summary>
    public PlanPoint ToPlan(double x, double y) => new(x + OffsetX, y + OffsetY);
}
