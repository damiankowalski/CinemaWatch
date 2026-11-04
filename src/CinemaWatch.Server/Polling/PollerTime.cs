using CinemaWatch.Core;

namespace CinemaWatch.Server.Polling;

/// <summary>Screening times are Polish wall-clock times; the database keeps UTC.</summary>
public static class PollerTime
{
    /// <summary>A Polish wall-clock time as UTC. A time skipped by the spring clock change counts as one hour later.</summary>
    public static DateTimeOffset ToUtc(DateTime polish)
    {
        var local = DateTime.SpecifyKind(polish, DateTimeKind.Unspecified);
        if (PolishTime.Zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, PolishTime.Zone), TimeSpan.Zero);
    }

    /// <summary>"14:38" in Polish time.</summary>
    public static string ClockText(DateTimeOffset utc) =>
        PolishTime.FromUtc(utc.UtcDateTime).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Links to the site in e-mails, built from Saas:BaseUrl (never from a request).</summary>
public static class SiteLinks
{
    public static string Home(string baseUrl) => Root(baseUrl) + "/";

    /// <summary>The profile list with this profile opened.</summary>
    public static string Profile(string baseUrl, Guid profileId) => $"{Root(baseUrl)}/?profile={profileId}#profile-{profileId}";

    private static string Root(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) && root.Scheme is "http" or "https"
            ? root.GetLeftPart(UriPartial.Authority)
            : throw new InvalidOperationException("Saas:BaseUrl must be an absolute http(s) address, e.g. http://localhost:5180");
}
