using CinemaWatch.Core;
using CinemaWatch.Server.Data;

namespace CinemaWatch.Server.Polling;

/// <summary>
/// Reads one screening's seats for a cycle (KB §5.4): the screening details, the hall layout (from the database, fetched
/// when missing or older than <see cref="PollerCycle.SeatPlanMaxAge"/>) and the free seats. Updates the row's stored details.
/// </summary>
internal sealed class ScreeningFetcher(CinemaCityClient client, AppDbContext db, PollerOptions options, DateTimeOffset now)
{
    /// <summary>With <see cref="PollerOptions.ReuseScreeningDetails"/>: details younger than this are reused.</summary>
    public static readonly TimeSpan DetailsMaxAge = TimeSpan.FromDays(1);

    private readonly Dictionary<(int, int), SeatPlan> _plans = [];

    /// <summary>Requests a check of this row will most likely need (for the request budget).</summary>
    public int EstimateRequests(WatchedScreeningRow row) =>
        1 + (CanReuseDetails(row) ? 0 : 1) + (row.VenueId is null ? 1 : 0);

    public async Task<ScreeningSeats> FetchAsync(WatchedScreeningRow row, CancellationToken cancellationToken)
    {
        Presentation presentation;
        if (CanReuseDetails(row))
        {
            presentation = StoredPresentation(row)!;
        }
        else
        {
            presentation = await client.GetPresentationAsync(row.Id, cancellationToken).ConfigureAwait(false);
            row.VenueId = presentation.VenueId;
            row.SeatplanId = presentation.SeatplanId;
            row.VenueTypeId = presentation.VenueTypeId;
            row.IsReserved = presentation.IsReserved;
            row.DetailsFetchedAt = now;
        }
        var plan = await GetPlanAsync(presentation.VenueId, presentation.SeatplanId, cancellationToken).ConfigureAwait(false);
        var free = await client.GetFreeSeatsAsync(presentation.Id, presentation.VenueTypeId, presentation.IsReserved, cancellationToken).ConfigureAwait(false);
        return new ScreeningSeats(presentation, plan, free);
    }

    /// <summary>The result of the row's last successful check, without a request; null when something is missing.</summary>
    public async Task<ScreeningSeats?> StoredAsync(WatchedScreeningRow row, CancellationToken cancellationToken)
    {
        if (row.FreeSeatKeys is null || StoredPresentation(row) is not { } presentation)
            return null;
        var plan = _plans.TryGetValue((presentation.VenueId, presentation.SeatplanId), out var known)
            ? known
            : (await SeatPlans.FindAsync(db, presentation.VenueId, presentation.SeatplanId, cancellationToken).ConfigureAwait(false))?.Plan;
        return plan is null ? null : new ScreeningSeats(presentation, plan, new HashSet<string>(row.FreeSeatKeys, StringComparer.Ordinal));
    }

    private bool CanReuseDetails(WatchedScreeningRow row) =>
        options.ReuseScreeningDetails && row.DetailsFetchedAt is { } fetched && now - fetched < DetailsMaxAge && StoredPresentation(row) is not null;

    private static Presentation? StoredPresentation(WatchedScreeningRow row) =>
        row is { VenueId: { } venue, SeatplanId: { } seatplan, VenueTypeId: { } venueType, IsReserved: { } reserved }
            ? new Presentation(row.Id, "", default, "", venue, seatplan, venueType, reserved, false, null)
            : null;

    private async Task<SeatPlan> GetPlanAsync(int venueId, int seatplanId, CancellationToken cancellationToken)
    {
        if (_plans.TryGetValue((venueId, seatplanId), out var plan))
            return plan;
        if (await SeatPlans.FindAsync(db, venueId, seatplanId, cancellationToken).ConfigureAwait(false) is { } stored
            && now - stored.FetchedAt < PollerCycle.SeatPlanMaxAge)
        {
            plan = stored.Plan;
        }
        else
        {
            var json = await client.GetSeatPlanJsonAsync(venueId, seatplanId, cancellationToken).ConfigureAwait(false);
            plan = await SeatPlans.StoreAsync(db, venueId, seatplanId, json, now, cancellationToken).ConfigureAwait(false);
        }
        _plans[(venueId, seatplanId)] = plan;
        return plan;
    }
}
