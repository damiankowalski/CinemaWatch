namespace CinemaWatch.Server.Polling;

/// <summary>
/// Cinema City data on demand for the wizard (configuration section "Saas:Poller:Wizard"; docs/SAAS.md §5.2). The wizard
/// sends its requests through the same guard as the poller (block detection, request log, the owner's pause) but has a
/// budget of its own, so a large search never slows the background checks, and every account has a limit of its own,
/// so one person cannot use the whole budget. The budget numbers can be changed in the operator's panel
/// (<see cref="PollerTuning"/>); the cache times only in the configuration.
/// </summary>
public sealed class WizardOptions
{
    /// <summary>Requests an hour for all wizard loadings together, refilled steadily (next to the poller's budget).</summary>
    public int RequestsPerHour { get; set; } = 120;

    /// <summary>
    /// Requests the wizard budget can save up. The largest desktop search (6 cinemas × 14 days = 84 requests) must fit
    /// without waiting.
    /// </summary>
    public int Burst { get; set; } = 90;

    /// <summary>Requests an hour one account may cause (only those that miss the cache count); saved up to the same number.</summary>
    public int AccountRequestsPerHour { get; set; } = 90;

    /// <summary>Pause before every wizard request after the first of a loading (one after the other, never in parallel).</summary>
    public double GapSeconds { get; set; } = 1;

    /// <summary>The repertoire of today and tomorrow (Polish time) is kept this long: it changes during the day.</summary>
    public int RepertoireNearMinutes { get; set; } = 30;

    /// <summary>The repertoire of later days is kept this long.</summary>
    public int RepertoireLaterMinutes { get; set; } = 180;

    /// <summary>Screening details (hall, layout) are kept this long.</summary>
    public int DetailsMinutes { get; set; } = 24 * 60;

    /// <summary>
    /// Free seats for the hall map: the poller's last result is used when it is younger than this; a fetched result is
    /// kept this long.
    /// </summary>
    public int FreeSeatsMinutes { get; set; } = 10;

    /// <summary>A finished loading (its result and progress) is kept this long for the page that shows it.</summary>
    public int KeepResultMinutes { get; set; } = 30;

    public TimeSpan Gap => TimeSpan.FromSeconds(GapSeconds);

    public bool IsValid() =>
        RequestsPerHour is >= 10 and <= 3600
        && Burst is >= 10 and <= 3600
        && AccountRequestsPerHour is >= 1 and <= 3600
        && GapSeconds is >= 0 and <= 60
        && RepertoireNearMinutes is >= 1 and <= 24 * 60
        && RepertoireLaterMinutes >= RepertoireNearMinutes && RepertoireLaterMinutes <= 7 * 24 * 60
        && DetailsMinutes is >= 1 and <= 7 * 24 * 60
        && FreeSeatsMinutes is >= 1 and <= 60
        && KeepResultMinutes is >= 1 and <= 24 * 60;

    public WizardOptions Copy() => (WizardOptions)MemberwiseClone();
}
