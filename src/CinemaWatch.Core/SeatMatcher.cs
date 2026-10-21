namespace CinemaWatch.Core;

/// <summary>Decides which watched seats count as "available" for a notification.</summary>
public static class SeatMatcher
{
    /// <summary>
    /// Returns the watched seats that are free now and form a run of at least <paramref name="minAdjacent"/>
    /// physically adjacent watched, free seats in the same row (ordered row by row, left to right).
    /// </summary>
    public static IReadOnlyList<Seat> FindAvailable(ScreeningSeats seats, IReadOnlySet<string> watchedKeys, int minAdjacent = 1)
    {
        minAdjacent = Math.Max(1, minAdjacent);
        var result = new List<Seat>();
        var candidates = seats.Plan.Seats
            .Where(s => watchedKeys.Contains(s.Key) && seats.IsFree(s))
            .GroupBy(s => (s.Section, s.RowKey));

        foreach (var row in candidates)
        {
            var run = new List<Seat>();
            foreach (var seat in row.OrderBy(s => s.Position))
            {
                if (run.Count > 0 && seat.Position != run[^1].Position + 1)
                {
                    if (run.Count >= minAdjacent) result.AddRange(run);
                    run.Clear();
                }
                run.Add(seat);
            }
            if (run.Count >= minAdjacent) result.AddRange(run);
        }
        return result;
    }
}
