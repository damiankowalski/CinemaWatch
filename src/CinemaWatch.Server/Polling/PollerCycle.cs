using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using CinemaWatch.Core;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Mail;
using CinemaWatch.Server.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Polling;

public enum CycleResult
{
    /// <summary>Nothing was due.</summary>
    Idle,

    /// <summary>The owner paused the checks, or a block is in force: nothing was sent.</summary>
    Paused,

    /// <summary>At least one due screening was handled.</summary>
    Worked,
}

/// <summary>What one cycle did (for the log and the tests).</summary>
public sealed record CycleReport(
    CycleResult Result,
    int Checked = 0,
    int Failed = 0,
    int FromStoredResult = 0,
    bool Blocked = false,
    bool BudgetExhausted = false,
    int Requests = 0,
    int MailsSent = 0,
    int MailsFailed = 0);

/// <summary>
/// One poller cycle (docs/SAAS.md §4.3):
/// <list type="number">
/// <item>read the effective settings (<see cref="PollerSettings"/>: the configuration plus the operator's changes);</item>
/// <item>bring watched_screenings in step with the profiles (<see cref="WatchList"/>);</item>
/// <item>stop if the owner paused the checks or a block is in force;</item>
/// <item>check the due screenings one by one - "Check now" first, then by due time - through one
/// <see cref="RequestGuard"/> (pause between requests, global budget, request log, block detection);</item>
/// <item>give each result to every profile that watches the screening and e-mail the newly available seats.</item>
/// </list>
/// Error rules: a failed check (HTTP 5xx, another 4xx, timeout, network, unreadable answer) changes no notification
/// state, is not retried and is tried again at the normal next time. A block (HTTP 403 / 429, a Cloudflare challenge,
/// HTML instead of JSON) ends the cycle at once, drops the pending "Check now" requests and pauses all requests
/// (<see cref="PollerOptions.BlockPause"/>, <see cref="UpstreamBlocks"/>); after the pause the blocked screening is
/// tried first, alone. A block the wizard ran into pauses the poller the same way (one address, one block). Each
/// screening's result is saved as soon as it is known, so the page can show a "Check now" filling in row by row.
/// One cycle runs at a time.
/// </summary>
public sealed class PollerCycle(
    IServiceScopeFactory scopes,
    ICinemaCityNetwork network,
    IRequestPacer pacer,
    RequestBudget budget,
    TimeProvider time,
    PollerSettings effective,
    IOptions<SaasOptions> saas,
    IMailSender mail,
    ServiceEvents events,
    UpstreamBlocks blocks,
    ILogger<PollerCycle> logger)
{
    /// <summary>Hall layouts are fetched again after this age.</summary>
    public static readonly TimeSpan SeatPlanMaxAge = TimeSpan.FromDays(7);

    /// <summary>Old log rows, history and events are deleted this often.</summary>
    public static readonly TimeSpan CleanupEvery = TimeSpan.FromDays(1);

    /// <summary>Stored layouts nobody used for this long are deleted.</summary>
    public static readonly TimeSpan SeatPlanRetention = TimeSpan.FromDays(30);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CycleReport> RunAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CycleReport> RunLockedAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await effective.RefreshAsync(db, cancellationToken).ConfigureAwait(false);
        var settings = effective.Poller;

        var state = await db.PollerState.SingleOrDefaultAsync(s => s.Id == PollerStateRow.SingleId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            state = new PollerStateRow();
            db.PollerState.Add(state);
        }
        if (state.LastCleanupAt is not { } cleaned || now - cleaned >= CleanupEvery)
        {
            await CleanUpAsync(db, settings, now, cancellationToken).ConfigureAwait(false);
            state.LastCleanupAt = now;
        }

        var watchers = await WatchList.SyncAsync(db, effective.Limits, now, logger, cancellationToken).ConfigureAwait(false);
        state.LastCycleAt = now;

        // Paused by the owner (no end time) or by a block: nothing is sent. A "Check now" that slipped in as the pause
        // began cannot run: the page shows the pause instead.
        if (state.PausedByOperatorAt is not null || state.BlockedUntil is { } until && until > now)
        {
            await db.WatchedScreenings.Where(r => r.ManualRequestedAt != null)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.ManualRequestedAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new CycleReport(CycleResult.Paused);
        }

        var due = (await db.WatchedScreenings
                .Where(r => r.ManualRequestedAt != null || r.NextCheckAt <= now)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(r => r.ManualRequestedAt is null)
            .ThenBy(r => r.ManualRequestedAt)
            .ThenBy(r => r.LastOutcome != CheckOutcome.Blocked) // after a pause the blocked screening is the one tried first
            .ThenBy(r => r.NextCheckAt)
            .ToList();
        if (due.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new CycleReport(CycleResult.Idle);
        }

        var cycle = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var recovering = state.BlockCount > 0;
        using var guard = new RequestGuard(network.CreateHandler(), pacer, budget, time);
        using var client = new CinemaCityClient(guard, disposeHandler: false, PollerInfo.UserAgent);
        var fetcher = new ScreeningFetcher(client, db, settings, now);

        var results = new Dictionary<string, ScreeningSeats>(StringComparer.Ordinal);
        int checkedCount = 0, failed = 0, fromStored = 0, waiting = 0;
        var budgetStop = false;
        foreach (var row in due)
        {
            var manual = row.ManualRequestedAt is not null;

            // "Check now" for a screening checked a moment ago and not due anyway: the stored result, no request.
            if (manual && row.NextCheckAt > now && row.LastSucceededAt is { } succeeded && now - succeeded < settings.ManualFresh
                && await fetcher.StoredAsync(row, cancellationToken).ConfigureAwait(false) is { } stored)
            {
                results[row.Id] = stored;
                row.ManualRequestedAt = null;
                fromStored++;
                continue;
            }
            if (budgetStop || guard.BlockedBy is not null || (recovering && checkedCount + failed > 0))
            {
                // After a block one screening first; the rest stays due for the next cycle.
                waiting++;
                continue;
            }
            if (budget.Available < fetcher.EstimateRequests(row))
            {
                budgetStop = true;
                waiting++;
                continue;
            }

            guard.CurrentScreeningId = row.Id;
            guard.CurrentManual = manual;
            try
            {
                var seats = await fetcher.FetchAsync(row, cancellationToken).ConfigureAwait(false);
                row.LastCheckedAt = now;
                row.LastSucceededAt = now;
                row.LastOutcome = CheckOutcome.Ok;
                row.LastHttpStatus = null;
                row.LastError = null;
                row.FreeSeatKeys = seats.FreeSeatKeys.Order(StringComparer.Ordinal).ToList();
                row.FreeCount = seats.FreeCount;
                row.TotalCount = seats.TotalCount;
                row.ManualRequestedAt = null;
                row.NextCheckAt = NextCheck(now, row.IntervalMinutes, settings);
                results[row.Id] = seats;
                checkedCount++;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); // the page shows each result at once
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (guard.BlockedBy is { } blocked)
                {
                    // Due again at once: after the pause this screening is the one tried first.
                    row.LastCheckedAt = now;
                    row.LastOutcome = CheckOutcome.Blocked;
                    row.LastHttpStatus = blocked.Status;
                    row.LastError = Shorten(blocked.Describe());
                    row.ManualRequestedAt = null;
                    row.NextCheckAt = now;
                    continue;
                }
                if (ex is BudgetExhaustedException)
                {
                    budgetStop = true;
                    waiting++;
                    continue;
                }

                // A failed check: notification state unchanged, no retry, the next attempt in the normal rhythm.
                row.LastCheckedAt = now;
                row.LastOutcome = CheckOutcome.Failed;
                row.LastHttpStatus = ex is CinemaCityException http ? (int)http.StatusCode : null;
                row.LastError = Shorten(Describe(ex));
                row.ManualRequestedAt = null;
                row.NextCheckAt = NextCheck(now, row.IntervalMinutes, settings);
                failed++;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                logger.LogWarning("Screening {ScreeningId}: check failed ({Error}); next attempt at {Next:O}", row.Id, row.LastError, row.NextCheckAt);
            }
        }

        foreach (var record in guard.Records)
            db.CheckLog.Add(ToLogEntry(record, cycle));

        if (guard.BlockedBy is { } blockedBy)
        {
            // Pending "Check now" requests are dropped: the users see the pause instead of a check that cannot run.
            foreach (var row in due)
                row.ManualRequestedAt = null;
            await blocks.RecordBlockAsync(db, state, guard, blockedBy, now, settings, cancellationToken).ConfigureAwait(false);
        }
        else if (recovering && checkedCount + failed > 0)
            await blocks.TryResumeAsync(db, state, cancellationToken).ConfigureAwait(false); // the wizard may have ended the block already
        if (budgetStop)
        {
            events.Add(db, ServiceEventKind.BudgetExhausted,
                $"Request budget used up ({settings.RequestsPerHour}/h): due screenings wait for the budget to refill", throttled: true);
            logger.LogWarning("Request budget used up ({PerHour}/h): {Waiting} due screening(s) wait", settings.RequestsPerHour, waiting);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var (sent, mailsFailed) = await NotifyAsync(db, watchers, results, now, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Cycle {Cycle}: {Checked} checked, {Failed} failed, {Stored} from the stored result, {Waiting} waiting, {Requests} request(s), {Sent} e-mail(s){Blocked}",
            cycle, checkedCount, failed, fromStored, waiting, guard.Records.Count, sent, guard.BlockedBy is null ? "" : ", BLOCKED");
        return new CycleReport(CycleResult.Worked, checkedCount, failed, fromStored, guard.BlockedBy is not null, budgetStop,
            guard.Records.Count, sent, mailsFailed);
    }

    /// <summary>Interval plus a random addition of up to <see cref="PollerOptions.JitterPercent"/>.</summary>
    public static DateTimeOffset NextCheck(DateTimeOffset now, int intervalMinutes, PollerOptions settings) =>
        now + TimeSpan.FromMinutes(intervalMinutes * (1 + Random.Shared.NextDouble() * settings.JitterPercent / 100.0));

    /// <summary>
    /// Gives every result to the profiles that watch the screening; e-mails each profile's newly available seats in one
    /// message. A profile's state is saved only after its e-mail went out; when the mail server refuses, the history
    /// records it and the same seats are notified at the next check.
    /// </summary>
    private async Task<(int Sent, int Failed)> NotifyAsync(
        AppDbContext db, IReadOnlyDictionary<string, List<Watcher>> watchers, IReadOnlyDictionary<string, ScreeningSeats> results,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var perProfile = new Dictionary<Guid, (StoredProfile Profile, WatchProfile Document, List<(WatchedScreening Screening, ScreeningSeats Seats)> Checks)>();
        foreach (var (screeningId, seats) in results)
        {
            if (!watchers.TryGetValue(screeningId, out var list))
                continue;
            foreach (var watcher in list)
            {
                if (!perProfile.TryGetValue(watcher.Profile.Id, out var entry))
                    perProfile[watcher.Profile.Id] = entry = (watcher.Profile, watcher.Document, []);
                entry.Checks.Add((watcher.Screening, seats));
            }
        }

        var polishNow = PolishTime.FromUtc(now.UtcDateTime);
        int sent = 0, failed = 0;
        foreach (var (profile, document, checks) in perProfile.Values)
        {
            var rows = await db.NotificationStates.Where(s => s.ProfileId == profile.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var state = new NotificationState
            {
                Available = rows.ToDictionary(r => r.ScreeningId, r => r.AvailableSeatKeys.ToList(), StringComparer.Ordinal),
            };
            var result = new ProfileCheckResult(polishNow, checks
                .Select(c => new ScreeningCheck(c.Screening, c.Seats,
                    SeatMatcher.FindAvailable(c.Seats, new HashSet<string>(c.Screening.SeatKeys, StringComparer.Ordinal), document.MinAdjacent),
                    null, IsPast: false))
                .ToList());
            var news = state.Update(result);

            if (news.Count > 0)
            {
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == profile.UserId, cancellationToken).ConfigureAwait(false);
                if (user?.Email is null)
                    continue;
                OutgoingMail message;
                string summary;
                using (Localizer.Use(AccountService.LanguageOf(user)))
                {
                    message = PollerMails.SeatsAvailable(user.Email, document, profile.Name, news, SiteLinks.Profile(saas.Value.BaseUrl, profile.Id));
                    summary = string.Join("\n", news.OrderBy(n => n.Screening.StartsAt).Select(n => PollerMails.ScreeningLine(n.Screening, n.NewSeats)));
                }
                var record = new NotificationRecord
                {
                    Id = Guid.CreateVersion7(now),
                    ProfileId = profile.Id,
                    CreatedAt = now,
                    Subject = Shorten(message.Subject, 300),
                    Summary = summary,
                };
                try
                {
                    await mail.SendAsync(message, cancellationToken).ConfigureAwait(false);
                    record.Delivered = true;
                    sent++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    record.Error = Shorten(ex.Message);
                    failed++;
                    logger.LogError(ex, "Notification for profile {ProfileId} could not be sent; the seats are notified again at the next check", profile.Id);
                    events.Add(db, ServiceEventKind.MailFailed, $"Notification not sent: {MailErrors.WithoutAddresses(ex.Message)}", throttled: true);
                }
                db.Notifications.Add(record);
                if (!record.Delivered)
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            foreach (var (screening, _) in checks)
            {
                var available = state.Available.TryGetValue(screening.Id, out var keys) ? keys : [];
                var row = rows.FirstOrDefault(r => r.ScreeningId == screening.Id);
                if (row is null)
                {
                    db.NotificationStates.Add(new NotificationStateRow { ProfileId = profile.Id, ScreeningId = screening.Id, AvailableSeatKeys = available, UpdatedAt = now });
                }
                else if (!row.AvailableSeatKeys.SequenceEqual(available))
                {
                    row.AvailableSeatKeys = available;
                    row.UpdatedAt = now;
                }
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return (sent, failed);
    }

    private static async Task CleanUpAsync(AppDbContext db, PollerOptions settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var logLimit = now - TimeSpan.FromDays(settings.CheckLogDays);
        var historyLimit = now - TimeSpan.FromDays(settings.HistoryDays);
        var planLimit = now - SeatPlanRetention;
        await db.CheckLog.Where(e => e.At < logLimit).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Notifications.Where(n => n.CreatedAt < historyLimit).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.ServiceEvents.Where(e => e.At < historyLimit).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.SeatPlans.Where(p => p.FetchedAt < planLimit).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CheckLogEntry ToLogEntry(RequestRecord r, string cycle) => new()
    {
        At = r.At,
        Cycle = cycle,
        ScreeningId = r.ScreeningId,
        Manual = r.Manual,
        Method = r.Method,
        Host = r.Host,
        Endpoint = r.Endpoint,
        Target = r.Target,
        Status = r.Status,
        DurationMs = r.DurationMs,
        ContentType = r.ContentType,
        CfMitigated = r.CfMitigated,
        CfRay = r.CfRay,
        RetryAfter = r.RetryAfter,
        Error = r.Error,
        Source = r.Source,
    };

    /// <summary>Technical description of a failed check (English, for the logs and the operator).</summary>
    public static string Describe(Exception exception) => exception switch
    {
        CinemaCityException e => $"HTTP {(int)e.StatusCode} on {e.Path}",
        TaskCanceledException or TimeoutException => "timeout",
        HttpRequestException e => $"network error: {e.Message}",
        JsonException => "unexpected response format",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

    private static string Shorten(string value, int max = 500) => value.Length <= max ? value : value[..max];
}
