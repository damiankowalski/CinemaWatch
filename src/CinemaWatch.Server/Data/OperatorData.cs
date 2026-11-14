namespace CinemaWatch.Server.Data;

/// <summary>
/// Poller settings the owner changed in the operator's panel (table poller_settings, one row). Every column is an
/// override: null means "the value from the configuration" (appsettings.json, environment variables). The ranges a
/// value may take are fixed in code (<see cref="Polling.PollerTuning"/>); the effective settings are built by
/// <see cref="Polling.PollerSettings"/>. Kept in the database, not in memory, so a separate worker would see them too.
/// </summary>
public sealed class PollerSettingsRow
{
    public const int SingleId = 1;

    public int Id { get; set; } = SingleId;

    public int? MinIntervalMinutes { get; set; }

    public int? RequestsPerHour { get; set; }

    public double? RequestGapMinSeconds { get; set; }

    public double? RequestGapMaxSeconds { get; set; }

    public double? ManualGapMinSeconds { get; set; }

    public double? ManualGapMaxSeconds { get; set; }

    public int? ManualFreshMinutes { get; set; }

    public int? JitterPercent { get; set; }

    public int? FirstBlockPauseMinutes { get; set; }

    public int? MaxBlockPauseMinutes { get; set; }

    public bool? ReuseScreeningDetails { get; set; }

    /// <summary>The wizard's own budget (docs/SAAS.md §5.2).</summary>
    public int? WizardRequestsPerHour { get; set; }

    public int? WizardBurst { get; set; }

    /// <summary>Requests an hour one account may cause through the wizard (cache misses only).</summary>
    public int? WizardAccountRequestsPerHour { get; set; }

    public double? WizardGapSeconds { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public bool IsEmpty =>
        MinIntervalMinutes is null && RequestsPerHour is null && RequestGapMinSeconds is null && RequestGapMaxSeconds is null
        && ManualGapMinSeconds is null && ManualGapMaxSeconds is null && ManualFreshMinutes is null && JitterPercent is null
        && FirstBlockPauseMinutes is null && MaxBlockPauseMinutes is null && ReuseScreeningDetails is null
        && WizardRequestsPerHour is null && WizardBurst is null && WizardAccountRequestsPerHour is null && WizardGapSeconds is null;

    public PollerSettingsRow Copy() => (PollerSettingsRow)MemberwiseClone();
}

/// <summary>
/// Last success and last failure of a part of the service that the database cannot see otherwise (table
/// component_status, one row per component): the mail server and the heartbeat. Kept in the database, so it survives
/// a restart and would work after a split into a web app and a worker.
/// </summary>
public sealed class ComponentStatusRow
{
    public const string Mail = "mail";
    public const string Heartbeat = "heartbeat";

    public string Component { get; set; } = "";

    public DateTimeOffset? LastOkAt { get; set; }

    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>Technical description of the last failure (English).</summary>
    public string? LastFailure { get; set; }
}
