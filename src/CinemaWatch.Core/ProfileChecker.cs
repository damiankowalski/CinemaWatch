namespace CinemaWatch.Core;

/// <summary>Result of checking one watched screening.</summary>
public sealed record ScreeningCheck(
    WatchedScreening Screening,
    ScreeningSeats? Seats,
    IReadOnlyList<Seat> Available,
    string? Error,
    bool IsPast);

public sealed record ProfileCheckResult(DateTime CheckedAt, IReadOnlyList<ScreeningCheck> Screenings)
{
    public bool HasMatches => Screenings.Any(s => s.Available.Count > 0);
    public bool AllFailed => Screenings.Any(s => !s.IsPast) && Screenings.Where(s => !s.IsPast).All(s => s.Error is not null);
}

/// <summary>Checks a profile against the current seat status (used by "Sprawdź teraz" and by --profile).</summary>
public static class ProfileChecker
{
    public static Task<ProfileCheckResult> CheckAsync(
        WatchProfile profile, SeatAvailabilityService service, DateTime now, CancellationToken cancellationToken = default) =>
        CheckAsync(profile, service.GetScreeningSeatsAsync, now, cancellationToken);

    /// <summary>Checks with a custom loader (e.g. shared between several profiles so each screening is fetched once).</summary>
    public static async Task<ProfileCheckResult> CheckAsync(
        WatchProfile profile,
        Func<string, CancellationToken, Task<ScreeningSeats>> loadSeats,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<ScreeningCheck>();
        foreach (var screening in profile.Screenings.OrderBy(s => s.StartsAt))
        {
            if (screening.StartsAt <= now)
            {
                checks.Add(new ScreeningCheck(screening, null, [], null, IsPast: true));
                continue;
            }

            try
            {
                var seats = await loadSeats(screening.Id, cancellationToken).ConfigureAwait(false);
                var watched = new HashSet<string>(screening.SeatKeys, StringComparer.Ordinal);
                var available = SeatMatcher.FindAvailable(seats, watched, profile.MinAdjacent);
                checks.Add(new ScreeningCheck(screening, seats, available, null, IsPast: false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                checks.Add(new ScreeningCheck(screening, null, [], ErrorMessages.Describe(ex), IsPast: false));
            }
        }
        return new ProfileCheckResult(now, checks);
    }
}
