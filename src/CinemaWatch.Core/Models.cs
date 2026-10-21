namespace CinemaWatch.Core;

/// <summary>A cinema location (e.g. Cinema City Sadyba).</summary>
public sealed record Cinema(string Id, string Name, string City);

/// <summary>A film listed in the repertoire.</summary>
public sealed record Film(string Id, string Name, int LengthMinutes, string? Link);

/// <summary>A single screening ("event") from the repertoire API.</summary>
public sealed record Screening(
    string Id,
    string FilmId,
    string CinemaId,
    DateTime StartsAt,
    string Auditorium,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<string> OriginalLanguages,
    IReadOnlyList<string> SubtitleLanguages,
    IReadOnlyList<string> DubbedLanguages,
    bool SoldOut,
    double AvailabilityRatio,
    string BookingLink)
{
    public bool HasAttribute(string attribute) =>
        Attributes.Contains(attribute, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Films and screenings of one cinema on one business day.</summary>
public sealed record DayRepertoire(DateOnly Date, IReadOnlyList<Film> Films, IReadOnlyList<Screening> Screenings);

/// <summary>Screening details from the ticketing API (needed to load the seat plan and seat status).</summary>
public sealed record Presentation(
    string Id,
    string FilmName,
    DateTime StartsAt,
    string VenueName,
    int VenueId,
    int SeatplanId,
    int VenueTypeId,
    int IsReserved,
    bool SoldOut,
    DateTime? TicketSaleStop);

/// <summary>
/// One seat of an auditorium. <see cref="Key"/> matches the keys returned by the seat status API
/// ("{section}_{position}_{row}"). <see cref="Position"/> grows from left to right, so consecutive
/// positions in the same row are physically adjacent. X/Y are seat centres in plan units.
/// <see cref="Row"/> and <see cref="Label"/> are what the website shows (e.g. row "8", seat "12").
/// </summary>
public sealed record Seat(
    string Key,
    string Section,
    string RowKey,
    string Row,
    string Label,
    int Position,
    double X,
    double Y,
    bool IsWheelchair);

/// <summary>A row of seats with optional positions of its left/right row labels.</summary>
public sealed record SeatRow(string Key, string Label, PlanPoint? LeftLabel, PlanPoint? RightLabel);

public readonly record struct PlanPoint(double X, double Y);

/// <summary>Static layout of an auditorium.</summary>
public sealed class SeatPlan
{
    public SeatPlan(double seatRadius, IReadOnlyList<SeatRow> rows, IReadOnlyList<Seat> seats)
    {
        SeatRadius = seatRadius;
        Rows = rows;
        Seats = seats;
    }

    public double SeatRadius { get; }
    public IReadOnlyList<SeatRow> Rows { get; }
    public IReadOnlyList<Seat> Seats { get; }

    public IEnumerable<Seat> SeatsInRow(string rowLabel) =>
        Seats.Where(s => s.Row == rowLabel).OrderBy(s => s.Position);
}

/// <summary>
/// Seat plan of a screening combined with the current set of free seats.
/// Counts cover regular seats only; wheelchair spaces (e.g. 1N/2N in row 12 of Sadyba IMAX) are reported
/// separately, because the website treats a screening as sold out even when they are still free.
/// </summary>
public sealed record ScreeningSeats(Presentation Presentation, SeatPlan Plan, IReadOnlySet<string> FreeSeatKeys)
{
    public int TotalCount => Plan.Seats.Count(s => !s.IsWheelchair);
    public int FreeCount => Plan.Seats.Count(s => !s.IsWheelchair && FreeSeatKeys.Contains(s.Key));
    public int WheelchairCount => Plan.Seats.Count(s => s.IsWheelchair);
    public int FreeWheelchairCount => Plan.Seats.Count(s => s.IsWheelchair && FreeSeatKeys.Contains(s.Key));
    public bool IsFree(Seat seat) => FreeSeatKeys.Contains(seat.Key);
}
