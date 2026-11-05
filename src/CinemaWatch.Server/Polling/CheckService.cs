using System.Collections.Concurrent;
using CinemaWatch.Core;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Mail;
using CinemaWatch.Server.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Server.Polling;

/// <summary>Whether the background checks run, until when they are paused by a block, and whether the owner paused them.</summary>
/// <param name="Resuming">The block's pause is over, but no request has got through yet (the poller is about to try).</param>
/// <param name="PausedByOperator">The owner paused all checks in the operator's panel (no end time).</param>
public sealed record PollerStatus(bool Enabled, DateTimeOffset? PausedUntil, bool Resuming = false, bool PausedByOperator = false)
{
    /// <summary>Nothing is sent to Cinema City now: "Check now" is unavailable.</summary>
    public bool IsPaused => PausedByOperator || PausedUntil is not null;
}

/// <summary>A watched screening as the profile page shows it.</summary>
/// <param name="Pending">A "Check now" for it is waiting or running.</param>
/// <param name="WatchedText">"rząd 8: 12–14 · rząd 9: cały rząd"; null until the hall layout is known.</param>
/// <param name="AvailableCount">
/// Watched seats free at the last successful check that count for this profile (its adjacent-seat rule); null before
/// the first successful check.
/// </param>
/// <param name="AvailableText">Those seats as text ("rząd 4: 18, 19").</param>
public sealed record ScreeningStatus(
    DateTimeOffset? LastCheckedAt,
    CheckOutcome? LastOutcome,
    int? LastHttpStatus,
    bool Pending,
    int? FreeCount,
    int? TotalCount,
    string? WatchedText,
    int? AvailableCount,
    string? AvailableText);

public sealed record NotificationView(DateTimeOffset At, bool Delivered, string Subject, string Summary);

/// <summary>A profile's checks as the profile page shows them.</summary>
/// <param name="Checking">A "Check now" of this profile is waiting or running: the button is unavailable until it ends.</param>
public sealed record ProfileStatus(
    IReadOnlyDictionary<string, ScreeningStatus> Screenings,
    DateTimeOffset? NextCheckAt,
    bool Checking,
    DateTimeOffset? LastManualCheckAt,
    IReadOnlyList<NotificationView> History);

public enum CheckRequestOutcome
{
    /// <summary>Accepted: the poller checks the profile's screenings within moments.</summary>
    Requested,

    /// <summary>A "Check now" of this profile is still waiting or running.</summary>
    AlreadyChecking,

    /// <summary>
    /// All requests are paused: by a block until <see cref="CheckRequestResult.Until"/>, or by the owner (Until is
    /// null).
    /// </summary>
    Paused,

    /// <summary>The profile is disabled or all its screenings are over.</summary>
    NotActive,

    /// <summary>The account has no such profile.</summary>
    NotFound,
}

public sealed record CheckRequestResult(CheckRequestOutcome Outcome, string Name = "", DateTimeOffset? Until = null);

public enum TestMailOutcome
{
    Sent,
    TooSoon,
    Failed,
}

public sealed record TestMailResult(TestMailOutcome Outcome, DateTimeOffset? AvailableAt = null, string? Error = null);

/// <summary>Remembers test e-mails per account in memory (a restart forgets: harmless).</summary>
public sealed class TestMailLimiter
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    /// <summary>Reserves the next test e-mail for the account, or says when it is possible again.</summary>
    public bool TryStart(string userId, DateTimeOffset now, out DateTimeOffset availableAt)
    {
        availableAt = default;
        while (true)
        {
            if (_last.TryGetValue(userId, out var last))
            {
                if (now - last < Interval)
                {
                    availableAt = last + Interval;
                    return false;
                }
                if (_last.TryUpdate(userId, now, last))
                    return true;
            }
            else if (_last.TryAdd(userId, now))
            {
                return true;
            }
        }
    }

    /// <summary>A failed e-mail does not count.</summary>
    public void Forget(string userId) => _last.TryRemove(userId, out _);
}

/// <summary>
/// The poller as the web pages see it: the state of a user's profiles and screenings, "Check now", the test e-mail and
/// whether checks are paused. Every operation takes the account's id; someone else's profile behaves as missing.
/// </summary>
public sealed class CheckService(
    AppDbContext db,
    PollerSettings settings,
    IOptions<PollerOptions> options,
    TimeProvider time,
    PollerSignal signal,
    IMailSender mail,
    IOptions<SaasOptions> saas,
    TestMailLimiter testMails,
    ILogger<CheckService> logger)
{
    /// <summary>Notifications shown per profile.</summary>
    public const int HistoryShown = 10;

    /// <summary>The effective poller settings (the configuration plus the operator's changes).</summary>
    public PollerOptions Settings => settings.Poller;

    public async Task<PollerStatus> GetPollerStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await db.PollerState.AsNoTracking()
            .Where(s => s.Id == PollerStateRow.SingleId)
            .Select(s => new { s.BlockedUntil, s.PausedByOperatorAt })
            .SingleOrDefaultAsync(cancellationToken);
        var until = state?.BlockedUntil;
        var byOperator = state?.PausedByOperatorAt is not null;
        var now = time.GetUtcNow();
        var enabled = options.Value.Enabled;
        return new PollerStatus(enabled, until is { } u && u > now ? u : null,
            enabled && !byOperator && until is { } over && over <= now, byOperator);
    }

    public async Task<IReadOnlyDictionary<Guid, ProfileStatus>> GetProfileStatusesAsync(
        string userId, IReadOnlyList<ProfileView> profiles, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var ids = profiles.Select(p => p.Id).ToList();
        var screeningIds = profiles.SelectMany(p => p.Document.Screenings.Select(s => s.Id)).Distinct().ToList();

        var stored = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.Id, p.LastManualCheckAt, p.CheckPending })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var rows = await db.WatchedScreenings.AsNoTracking()
            .Where(r => screeningIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, StringComparer.Ordinal, cancellationToken);
        var history = (await db.Notifications.AsNoTracking()
                .Where(n => ids.Contains(n.ProfileId))
                .OrderByDescending(n => n.CreatedAt)
                .ToListAsync(cancellationToken))
            .GroupBy(n => n.ProfileId)
            .ToDictionary(g => g.Key, g => g.Take(HistoryShown).Select(n => new NotificationView(n.CreatedAt, n.Delivered, n.Subject, n.Summary)).ToList());

        var plans = new Dictionary<(int, int), SeatPlan?>();
        foreach (var row in rows.Values)
        {
            if (row is { VenueId: { } venue, SeatplanId: { } seatplan } && !plans.ContainsKey((venue, seatplan)))
                plans[(venue, seatplan)] = (await SeatPlans.FindAsync(db, venue, seatplan, cancellationToken))?.Plan;
        }

        var polishNow = PolishTime.FromUtc(now.UtcDateTime);
        var result = new Dictionary<Guid, ProfileStatus>();
        foreach (var profile in profiles)
        {
            var screenings = new Dictionary<string, ScreeningStatus>(StringComparer.Ordinal);
            DateTimeOffset? next = null;
            var active = profile.Enabled && !profile.Ended;
            stored.TryGetValue(profile.Id, out var own);
            var requested = active && own is { CheckPending: true };
            var checking = requested;
            foreach (var screening in profile.Document.Screenings)
            {
                rows.TryGetValue(screening.Id, out var row);
                var plan = row is { VenueId: { } venue, SeatplanId: { } seatplan } ? plans.GetValueOrDefault((venue, seatplan)) : null;
                var watched = new HashSet<string>(screening.SeatKeys, StringComparer.Ordinal);
                string? watchedText = null, availableText = null;
                int? availableCount = null;
                if (plan is not null)
                {
                    watchedText = string.Join(" · ", SeatSelectionText.Describe(plan, watched).Select(r => $"{T("rząd", "row")} {r.Row}: {r.Text}"));
                    if (row is { FreeSeatKeys: { } freeKeys, LastSucceededAt: not null })
                    {
                        // What is free now for this profile, from the last result (the notification state only says what was e-mailed).
                        var seats = new ScreeningSeats(new Presentation(row.Id, "", default, "", 0, 0, 0, 0, false, null), plan,
                            new HashSet<string>(freeKeys, StringComparer.Ordinal));
                        var available = SeatMatcher.FindAvailable(seats, watched, profile.Document.MinAdjacent);
                        availableCount = available.Count;
                        availableText = NotificationBuilder.DescribeSeats(available);
                    }
                }
                var upcoming = screening.StartsAt > polishNow;
                var pending = active && upcoming && (requested || row?.ManualRequestedAt is not null);
                checking |= pending;
                screenings[screening.Id] = new ScreeningStatus(row?.LastCheckedAt, row?.LastOutcome, row?.LastHttpStatus, pending, row?.FreeCount, row?.TotalCount,
                    watchedText.NullIfEmpty(), availableCount, availableText.NullIfEmpty());
                if (row is not null && active)
                    next = next is null || row.NextCheckAt < next ? row.NextCheckAt : next;
            }
            result[profile.Id] = new ProfileStatus(screenings, next, checking, own?.LastManualCheckAt,
                history.TryGetValue(profile.Id, out var list) ? list : []);
        }
        return result;
    }

    /// <summary>
    /// "Check now" for a profile: not during a block or the owner's pause, not while the profile's previous "Check now" is still waiting or
    /// running, only for an enabled profile with upcoming screenings. The poller then decides per screening whether
    /// Cinema City is asked or a result younger than <see cref="PollerOptions.ManualFreshMinutes"/> is used.
    /// </summary>
    public async Task<CheckRequestResult> RequestCheckAsync(string userId, Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await db.Profiles.SingleOrDefaultAsync(p => p.Id == profileId && p.UserId == userId, cancellationToken);
        if (profile is null)
            return new CheckRequestResult(CheckRequestOutcome.NotFound);
        var now = time.GetUtcNow();
        if (!profile.Enabled || profile.EndsAt <= now)
            return new CheckRequestResult(CheckRequestOutcome.NotActive, profile.Name);
        var poller = await GetPollerStatusAsync(cancellationToken);
        if (poller.IsPaused)
            return new CheckRequestResult(CheckRequestOutcome.Paused, profile.Name, poller.PausedByOperator ? null : poller.PausedUntil);
        if (profile.CheckPending || await IsCheckingAsync(profile, cancellationToken))
            return new CheckRequestResult(CheckRequestOutcome.AlreadyChecking, profile.Name);

        profile.LastManualCheckAt = now;
        profile.CheckPending = true;
        await db.SaveChangesAsync(cancellationToken);
        signal.Wake();
        logger.LogInformation("User {UserId} asked to check profile {ProfileId} now", userId, profileId);
        return new CheckRequestResult(CheckRequestOutcome.Requested, profile.Name);
    }

    private async Task<bool> IsCheckingAsync(StoredProfile profile, CancellationToken cancellationToken)
    {
        var ids = ProfileStore.Parse(profile.Document).Screenings.Select(s => s.Id).ToList();
        return await db.WatchedScreenings.AnyAsync(r => ids.Contains(r.Id) && r.ManualRequestedAt != null, cancellationToken);
    }

    /// <summary>A sample notification to the account's own address (no Cinema City request), at most every 5 minutes.</summary>
    public async Task<TestMailResult> SendTestMailAsync(string userId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (!testMails.TryStart(userId, now, out var availableAt))
            return new TestMailResult(TestMailOutcome.TooSoon, availableAt);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user?.Email is null)
        {
            testMails.Forget(userId);
            return new TestMailResult(TestMailOutcome.Failed, Error: "no such account");
        }

        OutgoingMail message;
        using (Localizer.Use(AccountService.LanguageOf(user)))
            message = PollerMails.Test(user.Email, PolishTime.FromUtc(now.UtcDateTime), SiteLinks.Home(saas.Value.BaseUrl));
        try
        {
            await mail.SendAsync(message, cancellationToken);
            logger.LogInformation("Test e-mail sent for user {UserId}", userId);
            return new TestMailResult(TestMailOutcome.Sent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            testMails.Forget(userId);
            logger.LogError(ex, "Test e-mail for user {UserId} could not be sent", userId);
            return new TestMailResult(TestMailOutcome.Failed, Error: ex.Message);
        }
    }

    /// <summary>Notification history of the account's profiles (personal-data download), newest first.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationView>>> HistoryAsync(string userId, CancellationToken cancellationToken = default)
    {
        var ids = await db.Profiles.Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync(cancellationToken);
        var rows = await db.Notifications.AsNoTracking()
            .Where(n => ids.Contains(n.ProfileId))
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
        return rows.GroupBy(n => n.ProfileId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<NotificationView>)g.Select(n => new NotificationView(n.CreatedAt, n.Delivered, n.Subject, n.Summary)).ToList());
    }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? value) => string.IsNullOrEmpty(value) ? null : value;
}
