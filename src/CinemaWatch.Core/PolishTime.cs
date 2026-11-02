namespace CinemaWatch.Core;

/// <summary>
/// Screening times are local Polish time without an offset (Europe/Warsaw). The desktop app runs on a Polish PC and
/// can use <see cref="DateTime.Now"/>; any other client - the SaaS server runs in UTC - takes "now" from here.
/// </summary>
public static class PolishTime
{
    public static TimeZoneInfo Zone { get; } = FindZone();

    /// <summary>Current Polish wall-clock time (<see cref="DateTimeKind.Unspecified"/>, like the screening times).</summary>
    public static DateTime Now(TimeProvider? clock = null) => FromUtc((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);

    public static DateTime FromUtc(DateTime utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone), DateTimeKind.Unspecified);

    private static TimeZoneInfo FindZone()
    {
        // The IANA id works on Linux and macOS (tzdata) and on Windows with ICU; the Windows id covers Windows
        // without ICU (invariant globalization).
        foreach (var id in new[] { "Europe/Warsaw", "Central European Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Try the next id.
            }
        }
        throw new TimeZoneNotFoundException("The time zone Europe/Warsaw is not available on this system.");
    }
}
