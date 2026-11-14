using CinemaWatch.Core;
using CinemaWatch.Server.Profiles;

namespace CinemaWatch.Server.Wizard;

/// <summary>What a wizard loading fetches (docs/SAAS.md §5.2).</summary>
public abstract record WizardRequest;

/// <summary>Step 1 → 2: the repertoire of these cinemas on these days (one request per cinema and day).</summary>
public sealed record RepertoireRequest(IReadOnlyList<string> CinemaIds, DateOnly From, DateOnly To) : WizardRequest
{
    /// <summary>The desktop's ranges (DatesStepViewModel): 31 days for one cinema, 14 for several.</summary>
    public const int MaxDaysOneCinema = 31;

    public const int MaxDaysSeveralCinemas = 14;

    public int Days => To.DayNumber - From.DayNumber + 1;

    public IEnumerable<DateOnly> Dates => Enumerable.Range(0, Math.Max(0, Days)).Select(From.AddDays);
}

/// <summary>Step 2 → 3: what the hall map needs for these screenings (details, hall layout, free seats).</summary>
public sealed record ScreeningsRequest(IReadOnlyList<string> ScreeningIds) : WizardRequest;

/// <summary>Why a request is refused before anything is loaded.</summary>
public enum WizardRequestProblem
{
    NoCinema,
    UnknownCinema,
    DateInPast,
    DatesReversed,
    RangeTooLong,
    NoScreening,
    TooManyScreenings,
    BadScreeningId,
}

public static class WizardRequests
{
    /// <summary>
    /// Checks a request the way the desktop app does (cinemas from <see cref="Locations.ImaxCinemas"/>, no day before
    /// Polish today, the desktop's ranges, at most a profile's number of screenings); null when it is fine. The server
    /// checks every request: the form can be changed by anyone.
    /// </summary>
    public static WizardRequestProblem? Check(WizardRequest request, DateOnly polishToday, int maxScreenings) => request switch
    {
        RepertoireRequest r when r.CinemaIds.Count == 0 => WizardRequestProblem.NoCinema,
        RepertoireRequest r when r.CinemaIds.Distinct(StringComparer.Ordinal).Count() != r.CinemaIds.Count
                                 || r.CinemaIds.Any(id => Locations.FindCinema(id) is null) => WizardRequestProblem.UnknownCinema,
        RepertoireRequest r when r.From < polishToday => WizardRequestProblem.DateInPast,
        RepertoireRequest r when r.To < r.From => WizardRequestProblem.DatesReversed,
        RepertoireRequest r when r.Days > (r.CinemaIds.Count == 1 ? RepertoireRequest.MaxDaysOneCinema : RepertoireRequest.MaxDaysSeveralCinemas)
            => WizardRequestProblem.RangeTooLong,
        ScreeningsRequest s when s.ScreeningIds.Count == 0 => WizardRequestProblem.NoScreening,
        ScreeningsRequest s when s.ScreeningIds.Count > maxScreenings => WizardRequestProblem.TooManyScreenings,
        ScreeningsRequest s when s.ScreeningIds.Any(id => !ProfileImport.IsValidId(id))
                                 || s.ScreeningIds.Distinct(StringComparer.Ordinal).Count() != s.ScreeningIds.Count => WizardRequestProblem.BadScreeningId,
        _ => null,
    };
}

/// <summary>One cinema's day: the repertoire, or what went wrong (technical, English).</summary>
public sealed record RepertoireResult(string CinemaId, DateOnly Date, DayRepertoire? Day, string? Error);

/// <summary>
/// One screening for the hall map: its details (hall and layout; the film and time come from the repertoire), the
/// layout, the free seats and when they were read - or what went wrong (technical, English).
/// </summary>
public sealed record ScreeningResult(
    string ScreeningId,
    Presentation? Details,
    SeatPlan? Plan,
    IReadOnlySet<string>? FreeSeatKeys,
    DateTimeOffset? FreeSeatsAt,
    string? Error);

/// <summary>Free seats with the time they were read (cached for the hall map).</summary>
public sealed record FreeSeats(IReadOnlySet<string> Keys, DateTimeOffset At);

public enum WizardJobState
{
    Running,

    /// <summary>Every item was tried (some may have failed, see their errors).</summary>
    Finished,

    /// <summary>Stopped before the end (<see cref="WizardStopReason"/>); the items done so far are kept.</summary>
    Stopped,
}

/// <summary>Why a loading did not start, or stopped before the end.</summary>
public enum WizardStopReason
{
    /// <summary>The owner paused all requests in the operator's panel.</summary>
    PausedByOperator,

    /// <summary>Cinema City refused the server (now or earlier): a pause is in force.</summary>
    Blocked,

    /// <summary>The wizard's budget (all accounts together) is used up for now.</summary>
    BudgetUsedUp,

    /// <summary>This account's limit is used up for now.</summary>
    AccountLimit,

    /// <summary>The loading needs more requests than the limits can ever hold at once (they were lowered in the panel).</summary>
    TooLarge,

    /// <summary>The server is shutting down (a deployment, the nightly reboot).</summary>
    ServerStopping,
}

/// <summary>A loading as the page sees it: a copy that does not change.</summary>
public sealed record WizardJobView(
    Guid Id,
    WizardRequest Request,
    WizardJobState State,
    WizardStopReason? StopReason,
    DateTimeOffset? RetryAt,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int Total,
    int Done,
    int FromCache,
    int Failed,
    int Requests,
    IReadOnlyList<RepertoireResult> Repertoire,
    IReadOnlyList<ScreeningResult> Screenings);

public enum WizardStartOutcome
{
    /// <summary>The loading runs in the background (<see cref="WizardStart.Job"/>); it may already be finished.</summary>
    Started,

    /// <summary>This account already has a loading running (<see cref="WizardStart.Job"/>): one at a time.</summary>
    AlreadyRunning,

    /// <summary>The request breaks a rule (<see cref="WizardStart.Problem"/>); nothing is sent.</summary>
    Invalid,

    /// <summary>Not started (<see cref="WizardStart.Reason"/>, <see cref="WizardStart.RetryAt"/>); nothing is sent, nothing queued.</summary>
    Refused,
}

public sealed record WizardStart(
    WizardStartOutcome Outcome,
    WizardJobView? Job = null,
    WizardRequestProblem? Problem = null,
    WizardStopReason? Reason = null,
    DateTimeOffset? RetryAt = null);
