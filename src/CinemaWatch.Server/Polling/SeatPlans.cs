using System.Collections.Concurrent;
using CinemaWatch.Core;
using CinemaWatch.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace CinemaWatch.Server.Polling;

/// <summary>Hall layouts stored in the database (table seat_plans), parsed once per stored version.</summary>
public static class SeatPlans
{
    private static readonly ConcurrentDictionary<(int VenueId, int SeatplanId), (DateTimeOffset FetchedAt, SeatPlan Plan)> Parsed = new();

    /// <summary>The stored layout whatever its age, or null when none is stored (or it cannot be read).</summary>
    public static async Task<(SeatPlan Plan, DateTimeOffset FetchedAt)?> FindAsync(AppDbContext db, int venueId, int seatplanId, CancellationToken cancellationToken = default)
    {
        var key = (venueId, seatplanId);
        var fetchedAt = await db.SeatPlans.AsNoTracking()
            .Where(p => p.VenueId == venueId && p.SeatplanId == seatplanId)
            .Select(p => (DateTimeOffset?)p.FetchedAt)
            .SingleOrDefaultAsync(cancellationToken);
        if (fetchedAt is null)
            return null;
        if (Parsed.TryGetValue(key, out var cached) && cached.FetchedAt == fetchedAt)
            return (cached.Plan, cached.FetchedAt);

        var json = await db.SeatPlans.AsNoTracking()
            .Where(p => p.VenueId == venueId && p.SeatplanId == seatplanId)
            .Select(p => p.Json)
            .SingleAsync(cancellationToken);
        try
        {
            var plan = CinemaCityParser.ParseSeatPlan(json);
            Parsed[key] = (fetchedAt.Value, plan);
            return (plan, fetchedAt.Value);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Stores a freshly fetched layout (insert or replace).</summary>
    public static async Task<SeatPlan> StoreAsync(AppDbContext db, int venueId, int seatplanId, string json, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var plan = CinemaCityParser.ParseSeatPlan(json);
        var row = await db.SeatPlans.FindAsync([venueId, seatplanId], cancellationToken);
        if (row is null)
            db.SeatPlans.Add(new SeatPlanRow { VenueId = venueId, SeatplanId = seatplanId, Json = json, FetchedAt = now });
        else
        {
            row.Json = json;
            row.FetchedAt = now;
        }
        Parsed[(venueId, seatplanId)] = (now, plan);
        return plan;
    }
}
