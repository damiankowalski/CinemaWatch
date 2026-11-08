using CinemaWatch.Core;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Polling;

/// <summary>Whether requests to Cinema City may go out now (poller_state; the same for the poller and the wizard).</summary>
public sealed record UpstreamStatus(DateTimeOffset? PausedByOperatorAt, DateTimeOffset? BlockedUntil, int BlockCount)
{
    public static UpstreamStatus Free { get; } = new(null, null, 0);

    public bool IsPausedByOperator => PausedByOperatorAt is not null;

    public bool IsBlockedAt(DateTimeOffset now) => BlockedUntil is { } until && until > now;

    /// <summary>A block was recorded and its pause is over, but no request has got through since.</summary>
    public bool IsRecoveringAt(DateTimeOffset now) => BlockCount > 0 && !IsBlockedAt(now);
}

/// <summary>
/// What happens when Cinema City refuses the server and when requests get through again - the same for the poller and
/// the wizard, because both send from the same address (docs/SAAS.md §9): the pause after a block (doubling, a longer
/// Retry-After respected), the service event and the owner's alert; and, once a request gets through, the end of the
/// block. The block state lives in poller_state, so it survives a restart.
/// </summary>
public sealed class UpstreamBlocks(
    IOptions<SaasOptions> saas,
    IMailSender mail,
    ServiceEvents events,
    ILogger<UpstreamBlocks> logger)
{
    /// <summary>A Retry-After longer than this is not believed.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromDays(7);

    /// <summary>Serialises the wizard's changes to poller_state (the poller holds its own row through a cycle).</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public static async Task<UpstreamStatus> ReadAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var state = await db.PollerState.AsNoTracking()
            .Where(s => s.Id == PollerStateRow.SingleId)
            .Select(s => new UpstreamStatus(s.PausedByOperatorAt, s.BlockedUntil, s.BlockCount))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return state ?? UpstreamStatus.Free;
    }

    /// <summary>
    /// Records a block in <paramref name="state"/> (tracked by <paramref name="db"/>; the caller saves): the pause, an
    /// event with the start of the blocked answer, the owner's alert. Returns the end of the pause.
    /// </summary>
    public async Task<DateTimeOffset> RecordBlockAsync(
        AppDbContext db, PollerStateRow state, RequestGuard guard, RequestRecord blocked, DateTimeOffset now, PollerOptions settings,
        CancellationToken cancellationToken)
    {
        state.BlockCount++;
        var pause = settings.BlockPause(state.BlockCount);
        if (guard.RetryAfter is { } retryAfter && retryAfter > pause)
            pause = retryAfter < MaxRetryAfter ? retryAfter : MaxRetryAfter;
        var until = now + pause;
        var reason = blocked.Describe();
        state.BlockedUntil = until;
        state.BlockedAt ??= now;
        state.BlockReason = Shorten(reason);

        var who = blocked.Source == RequestSource.Wizard ? "Wizard: " : "";
        events.Add(db, ServiceEventKind.Blocked,
            $"{who}{reason}; no requests until {until:O} (block {state.BlockCount} in a row)", guard.BlockedBody);
        logger.LogWarning("BLOCKED ({Source}): {Reason}; no requests until {Until:O} (block {Count} in a row)", blocked.Source, reason, until, state.BlockCount);
        var count = state.BlockCount;
        await AlertOwnerAsync(db, () => PollerMails.Blocked("", reason, until, count, SiteLinks.Home(saas.Value.BaseUrl)), cancellationToken)
            .ConfigureAwait(false);
        return until;
    }

    /// <summary>
    /// Ends a recorded block after a request got through: in the database at once and only once - when the poller and
    /// the wizard both get through, the first one records the event and alerts the owner, the other does nothing.
    /// <paramref name="tracked"/> (the poller's row for its cycle) is brought in step without being marked changed. The
    /// event is added to <paramref name="db"/>; the caller saves. False when there was no block to end.
    /// </summary>
    public async Task<bool> TryResumeAsync(AppDbContext db, PollerStateRow? tracked, CancellationToken cancellationToken)
    {
        var since = await db.PollerState.AsNoTracking().Where(s => s.Id == PollerStateRow.SingleId)
            .Select(s => s.BlockedAt).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var ended = await db.PollerState.Where(s => s.Id == PollerStateRow.SingleId && s.BlockCount > 0)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.BlockCount, 0)
                .SetProperty(s => s.BlockedUntil, (DateTimeOffset?)null)
                .SetProperty(s => s.BlockedAt, (DateTimeOffset?)null)
                .SetProperty(s => s.BlockReason, (string?)null), cancellationToken).ConfigureAwait(false);
        if (tracked is not null)
        {
            var entry = db.Entry(tracked);
            SetUnchanged(entry.Property(s => s.BlockCount), 0);
            SetUnchanged(entry.Property(s => s.BlockedUntil), null);
            SetUnchanged(entry.Property(s => s.BlockedAt), null);
            SetUnchanged(entry.Property(s => s.BlockReason), null);
        }
        if (ended == 0)
            return false;

        events.Add(db, ServiceEventKind.Resumed, since is null ? "Requests get through again" : $"Requests get through again; the block started at {since:O}");
        logger.LogInformation("Resumed: requests get through again");
        await AlertOwnerAsync(db, () => PollerMails.Resumed("", since, SiteLinks.Home(saas.Value.BaseUrl)), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void SetUnchanged<T>(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<PollerStateRow, T> property, T value)
    {
        property.OriginalValue = value;
        property.CurrentValue = value;
        property.IsModified = false;
    }

    /// <summary>An alert to the owner (Saas:OwnerEmail) in the owner's language; a mail failure is logged, never thrown.</summary>
    private async Task AlertOwnerAsync(AppDbContext db, Func<OutgoingMail> build, CancellationToken cancellationToken)
    {
        var ownerEmail = saas.Value.OwnerEmail.Trim();
        if (ownerEmail.Length == 0)
            return;
        var normalized = ownerEmail.ToUpperInvariant();
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken).ConfigureAwait(false);
        OutgoingMail message;
        using (Localizer.Use(owner is null ? AppLanguage.Polish : AccountService.LanguageOf(owner)))
            message = build() with { To = ownerEmail };
        try
        {
            await mail.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Alert to the owner could not be sent: {Subject}", message.Subject);
            events.Add(db, ServiceEventKind.MailFailed, $"Alert \"{message.Subject}\" not sent: {MailErrors.WithoutAddresses(ex.Message)}", throttled: true);
        }
    }

    private static string Shorten(string value, int max = 500) => value.Length <= max ? value : value[..max];
}
