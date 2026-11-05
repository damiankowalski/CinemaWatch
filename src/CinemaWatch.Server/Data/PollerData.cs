namespace CinemaWatch.Server.Data;

/// <summary>Result of the last attempt to check a screening.</summary>
public enum CheckOutcome
{
    /// <summary>The free seats were read.</summary>
    Ok,

    /// <summary>
    /// The check failed without a sign of blocking: HTTP 5xx, another 4xx (e.g. 404), a timeout, a network error or an
    /// unreadable answer. The notification state stays as it was; the next attempt follows the normal schedule.
    /// </summary>
    Failed,

    /// <summary>Cinema City (Cloudflare) refused the server: HTTP 403 / 429, a challenge, or HTML instead of JSON.</summary>
    Blocked,
}

/// <summary>
/// One unique screening watched by at least one enabled profile (table watched_screenings). The poller works on this
/// table, not on the profiles: each screening is checked once and the result goes to every profile that watches it.
/// The rows are derived from the profiles at every poller cycle (added, updated, removed), so nothing else has to keep
/// them in step. All times are UTC.
/// </summary>
public sealed class WatchedScreeningRow
{
    /// <summary>Screening id (= presentation id), unique across all cinemas.</summary>
    public string Id { get; set; } = "";

    public string CinemaId { get; set; } = "";

    /// <summary>Start of the screening (UTC); the row is removed once it has started.</summary>
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>Shortest effective interval of the profiles that watch it (never below the server minimum).</summary>
    public int IntervalMinutes { get; set; }

    public DateTimeOffset NextCheckAt { get; set; }

    /// <summary>Set when somebody pressed "Check now" for a profile that watches it; cleared by the next cycle.</summary>
    public DateTimeOffset? ManualRequestedAt { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    public DateTimeOffset? LastSucceededAt { get; set; }

    public CheckOutcome? LastOutcome { get; set; }

    /// <summary>HTTP status of a failed or blocked check, if there was a response.</summary>
    public int? LastHttpStatus { get; set; }

    /// <summary>Technical description of the last failure (English, for the logs and the operator).</summary>
    public string? LastError { get; set; }

    /// <summary>Free seat keys of the last successful check (all seats of the hall, not only the watched ones).</summary>
    public List<string>? FreeSeatKeys { get; set; }

    /// <summary>Free regular seats / all regular seats of the hall at the last successful check.</summary>
    public int? FreeCount { get; set; }

    public int? TotalCount { get; set; }

    // Screening details from the presentation endpoint: they locate the hall layout and the free-seat request.
    public int? VenueId { get; set; }

    public int? SeatplanId { get; set; }

    public int? VenueTypeId { get; set; }

    public int? IsReserved { get; set; }

    public DateTimeOffset? DetailsFetchedAt { get; set; }
}

/// <summary>
/// Hall layout as Cinema City returned it (table seat_plans), shared by all users. Layouts are static per
/// venue / seat plan; one older than <see cref="Polling.PollerCycle.SeatPlanMaxAge"/> is fetched again when needed.
/// </summary>
public sealed class SeatPlanRow
{
    public int VenueId { get; set; }

    public int SeatplanId { get; set; }

    public string Json { get; set; } = "";

    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>
/// Watched seats that were available at the last successful check, per profile and screening (table
/// notification_state; docs/KNOWLEDGE_BASE.md §4 "notify only about new seats"). Saved only after the e-mail went out.
/// </summary>
public sealed class NotificationStateRow
{
    public Guid ProfileId { get; set; }

    public string ScreeningId { get; set; } = "";

    public List<string> AvailableSeatKeys { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One notification e-mail (table notifications): the profile's history; kept for a limited time.</summary>
public sealed class NotificationRecord
{
    public Guid Id { get; set; }

    public Guid ProfileId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>False when the mail server refused it; the seats are then notified again at the next check.</summary>
    public bool Delivered { get; set; }

    public string Subject { get; set; } = "";

    /// <summary>One line per screening, as in the e-mail (in the account's language at the time).</summary>
    public string Summary { get; set; } = "";

    public string? Error { get; set; }
}

/// <summary>
/// One request to Cinema City (table check_log): the same columns as the R1 probe's CSV, so the numbers can be
/// compared. No personal data. Kept for a limited time.
/// </summary>
public sealed class CheckLogEntry
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Poller cycle, e.g. 20261103T144400Z.</summary>
    public string Cycle { get; set; } = "";

    public string? ScreeningId { get; set; }

    /// <summary>True when the check was started by "Check now".</summary>
    public bool Manual { get; set; }

    public string Method { get; set; } = "";

    public string Host { get; set; } = "";

    /// <summary>presentation, seatplan, seats-status, …</summary>
    public string Endpoint { get; set; } = "";

    public string Target { get; set; } = "";

    public int? Status { get; set; }

    public long DurationMs { get; set; }

    public string? ContentType { get; set; }

    public string? CfMitigated { get; set; }

    public string? CfRay { get; set; }

    public string? RetryAfter { get; set; }

    public string? Error { get; set; }
}

/// <summary>State of the poller that must survive a restart (table poller_state, one row).</summary>
public sealed class PollerStateRow
{
    public const int SingleId = 1;

    public int Id { get; set; } = SingleId;

    /// <summary>No request is sent before this time (a block is in force).</summary>
    public DateTimeOffset? BlockedUntil { get; set; }

    /// <summary>Blocks in a row; 0 after a cycle that reached Cinema City without a block. Doubles the pause.</summary>
    public int BlockCount { get; set; }

    public DateTimeOffset? BlockedAt { get; set; }

    public string? BlockReason { get; set; }

    public DateTimeOffset? LastCycleAt { get; set; }

    public DateTimeOffset? LastCleanupAt { get; set; }

    /// <summary>
    /// The owner paused all checks in the operator's panel at this time; null = not paused. Unlike a block, the pause
    /// has no end: it lasts until the owner resumes the checks.
    /// </summary>
    public DateTimeOffset? PausedByOperatorAt { get; set; }
}

public enum ServiceEventKind
{
    PollerStarted,
    Blocked,
    Resumed,
    BudgetExhausted,
    MailFailed,
    CycleFailed,

    /// <summary>The owner changed the poller settings in the operator's panel.</summary>
    SettingsChanged,

    /// <summary>The owner paused all checks.</summary>
    PausedByOperator,

    /// <summary>The owner resumed the checks.</summary>
    ResumedByOperator,

    /// <summary>The heartbeat (the external "dead man's switch") could not be reached.</summary>
    HeartbeatFailed,
}

/// <summary>
/// Something the operator should be able to look back at (table service_events): blocks, resumptions, an exhausted
/// request budget, mail failures, the owner's settings changes and pauses. Shown in the operator's panel; kept for a
/// limited time.
/// </summary>
public sealed class ServiceEvent
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public ServiceEventKind Kind { get; set; }

    /// <summary>Short technical description (English).</summary>
    public string Message { get; set; } = "";

    /// <summary>Optional details, e.g. the start of a blocked response.</summary>
    public string? Data { get; set; }
}
