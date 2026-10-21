using System.Collections.Concurrent;
using System.Text.Json;

namespace CinemaWatch.Core;

/// <summary>
/// Loads the seat plan and the current free seats of a screening. Seat plans are static per auditorium, so they
/// are cached in memory and - when <paramref name="cacheDirectory"/> is given - on disk for
/// <see cref="SeatPlanCacheAge"/>. Each background check then costs only two requests per screening.
/// </summary>
public sealed class SeatAvailabilityService(CinemaCityClient client, string? cacheDirectory = null)
{
    public static readonly TimeSpan SeatPlanCacheAge = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<(int VenueId, int SeatplanId), Task<SeatPlan>> _plans = new();

    public async Task<ScreeningSeats> GetScreeningSeatsAsync(string presentationId, CancellationToken cancellationToken = default)
    {
        var presentation = await client.GetPresentationAsync(presentationId, cancellationToken).ConfigureAwait(false);
        var plan = await GetSeatPlanAsync(presentation.VenueId, presentation.SeatplanId, cancellationToken).ConfigureAwait(false);
        var free = await client.GetFreeSeatsAsync(presentation.Id, presentation.VenueTypeId, presentation.IsReserved, cancellationToken)
            .ConfigureAwait(false);
        return new ScreeningSeats(presentation, plan, free);
    }

    private async Task<SeatPlan> GetSeatPlanAsync(int venueId, int seatplanId, CancellationToken cancellationToken)
    {
        var key = (venueId, seatplanId);
        var task = _plans.GetOrAdd(key, k => LoadSeatPlanAsync(k.VenueId, k.SeatplanId));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (task.IsFaulted || task.IsCanceled)
        {
            // Do not cache failures - the next call should try again.
            _plans.TryRemove(new KeyValuePair<(int, int), Task<SeatPlan>>(key, task));
            throw;
        }
    }

    private async Task<SeatPlan> LoadSeatPlanAsync(int venueId, int seatplanId)
    {
        var file = cacheDirectory is null ? null : Path.Combine(cacheDirectory, $"seatplan-{venueId}-{seatplanId}.json");
        if (file is not null && File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < SeatPlanCacheAge)
        {
            try
            {
                return CinemaCityParser.ParseSeatPlan(await File.ReadAllTextAsync(file).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Broken cache entry - fetch again below.
            }
        }

        var json = await client.GetSeatPlanJsonAsync(venueId, seatplanId).ConfigureAwait(false);
        var plan = CinemaCityParser.ParseSeatPlan(json);
        if (file is not null)
        {
            try
            {
                Directory.CreateDirectory(cacheDirectory!);
                await File.WriteAllTextAsync(file, json).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Cache is an optimisation only.
            }
        }
        return plan;
    }
}
