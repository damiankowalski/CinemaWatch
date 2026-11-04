namespace CinemaWatch.Server.Polling;

/// <summary>Where the poller sends its requests.</summary>
public enum CinemaCitySource
{
    /// <summary>The real Cinema City API.</summary>
    CinemaCity,

    /// <summary>
    /// Captured responses steered by files in <see cref="PollerOptions.FakeDirectory"/> (<see cref="FakeCinemaCityHandler"/>).
    /// Development and tests only: the app refuses to start with it in any other environment.
    /// </summary>
    Fake,
}

/// <summary>
/// Settings of the poller (configuration section "Saas:Poller"; docs/SAAS.md §4.3, KB §3.6). The intervals per screening
/// come from the profiles and <see cref="Profiles.LimitOptions.MinIntervalMinutes"/>; these settings shape how the
/// requests go out.
/// </summary>
public sealed class PollerOptions
{
    public const string Section = "Saas:Poller";

    /// <summary>False: no background checks at all (the tests drive the cycles themselves).</summary>
    public bool Enabled { get; set; } = true;

    public CinemaCitySource Source { get; set; } = CinemaCitySource.CinemaCity;

    /// <summary>Folder that steers the fake (only with <see cref="CinemaCitySource.Fake"/>).</summary>
    public string FakeDirectory { get; set; } = "";

    /// <summary>How often the poller looks for due screenings; "Check now" wakes it earlier.</summary>
    public int TickSeconds { get; set; } = 30;

    /// <summary>Random addition to every interval, in percent (10 min + up to 20% = 10-12 min): no fixed rhythm.</summary>
    public int JitterPercent { get; set; } = 20;

    /// <summary>
    /// Random pause before the next screening of a background cycle: never a burst, even when several screenings are
    /// due at once. The requests of one screening (details, then free seats) follow each other directly, like a browser
    /// opening the screening's page.
    /// </summary>
    public double RequestGapMinSeconds { get; set; } = 3;

    public double RequestGapMaxSeconds { get; set; } = 6;

    /// <summary>The same pause for "Check now", where somebody waits for the result.</summary>
    public double ManualGapMinSeconds { get; set; } = 1;

    public double ManualGapMaxSeconds { get; set; } = 2;

    /// <summary>
    /// Global request budget of the whole server (all users, scheduled and manual checks). It refills steadily; when
    /// it is used up, due screenings wait in the order of their due times, so everybody is checked a little less
    /// often instead of the server sending more.
    /// </summary>
    public int RequestsPerHour { get; set; } = 120;

    /// <summary>
    /// "Check now" does not ask Cinema City about a screening checked this recently; the stored result is used at once.
    /// 0 = always ask. The button itself has no cooldown: the pause between requests and the global budget limit what
    /// reaches Cinema City, however often it is pressed.
    /// </summary>
    public int ManualFreshMinutes { get; set; } = 1;

    /// <summary>Pause after the first block; it doubles with every block in a row.</summary>
    public int FirstBlockPauseMinutes { get; set; } = 60;

    public int MaxBlockPauseMinutes { get; set; } = 24 * 60;

    /// <summary>
    /// False (default): every check asks for the screening details and then for the free seats - the same pair of
    /// requests the R1 probe measured. True: details younger than a day are reused, so a check needs one request.
    /// </summary>
    public bool ReuseScreeningDetails { get; set; }

    /// <summary>Days the request log (check_log) is kept.</summary>
    public int CheckLogDays { get; set; } = 30;

    /// <summary>Days the notification history and the service events are kept.</summary>
    public int HistoryDays { get; set; } = 90;

    public TimeSpan Tick => TimeSpan.FromSeconds(TickSeconds);

    public TimeSpan ManualFresh => TimeSpan.FromMinutes(ManualFreshMinutes);

    public bool IsValid() =>
        TickSeconds is >= 5 and <= 600
        && JitterPercent is >= 0 and <= 100
        && RequestGapMinSeconds >= 0
        && RequestGapMaxSeconds >= RequestGapMinSeconds
        && RequestGapMaxSeconds <= 60
        && ManualGapMinSeconds >= 0
        && ManualGapMaxSeconds >= ManualGapMinSeconds
        && ManualGapMaxSeconds <= 60
        && RequestsPerHour is >= 10 and <= 3600
        && ManualFreshMinutes >= 0
        && FirstBlockPauseMinutes >= 1
        && MaxBlockPauseMinutes >= FirstBlockPauseMinutes
        && CheckLogDays >= 1
        && HistoryDays >= 1
        && (Source != CinemaCitySource.Fake || !string.IsNullOrWhiteSpace(FakeDirectory));

    /// <summary>Pause after the n-th block in a row: first, then doubled, at most the maximum.</summary>
    public TimeSpan BlockPause(int blocksInARow)
    {
        var minutes = (double)FirstBlockPauseMinutes * Math.Pow(2, Math.Clamp(blocksInARow - 1, 0, 20));
        return TimeSpan.FromMinutes(Math.Min(minutes, MaxBlockPauseMinutes));
    }
}
