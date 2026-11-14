using CinemaWatch.Core;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CinemaWatch.Server.Wizard;

/// <summary>Thrown inside a loading when the owner's pause or a block means nothing may be sent.</summary>
public sealed class WizardStoppedException(WizardStopReason reason, DateTimeOffset? retryAt)
    : Exception($"Not sent: {reason}.")
{
    public WizardStopReason Reason { get; } = reason;

    public DateTimeOffset? RetryAt { get; } = retryAt;
}

/// <summary>One thing a loading fetches: a cinema's day, or one screening for the hall map.</summary>
public sealed record WizardItem(string Key, string? CinemaId = null, DateOnly? Date = null, string? ScreeningId = null);

/// <summary>
/// The wizard's Cinema City data on demand (docs/SAAS.md §5.2, stage 6.4.2): loadings that run in the background and
/// report their progress, because a large search takes over a minute and Cloudflare ends a proxied request after
/// 100 s. Rules:
/// <list type="bullet">
/// <item>only for signed-in accounts, read-only, one loading per account at a time;</item>
/// <item>answers come from the shared cache (<see cref="CinemaCityCache"/>) where possible; identical loads running at
/// the same time go out once;</item>
/// <item>requests that miss the cache go out one after the other (<see cref="WizardOptions.GapSeconds"/> apart) through
/// the same guard as the poller's: block detection, the request log (source "Wizard"), a fresh session per loading;</item>
/// <item>they take tokens from the wizard's budget and the account's limit: what the loading is expected to need is
/// reserved at the start, so a started loading finishes;</item>
/// <item>the owner's pause, a block in force or a budget that is used up: nothing is sent and the answer says when to
/// try again; there is no queue;</item>
/// <item>a block the wizard runs into pauses the poller too (one address), alerts the owner and stops the loading;</item>
/// <item>a failed item (HTTP 5xx, timeout) is reported and the loading goes on, like the desktop app.</item>
/// </list>
/// Loadings live in memory: a restart forgets them (the page starts a new one).
/// </summary>
public sealed class WizardJobs(
    IServiceScopeFactory scopes,
    ICinemaCityNetwork network,
    IWizardPacer pacer,
    WizardBudget budget,
    AccountBudgets accounts,
    CinemaCityCache cache,
    PollerSettings settings,
    UpstreamBlocks blocks,
    ServiceEvents events,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<WizardJobs> logger)
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Job> _jobs = [];

    // For the nested Loading (primary constructor parameters are not members).
    private IServiceScopeFactory Scopes => scopes;
    private CinemaCityCache Cache => cache;
    private PollerSettings Settings => settings;
    private UpstreamBlocks Blocks => blocks;
    private TimeProvider Time => time;

    /// <summary>Loadings running now (all accounts; for the operator's panel).</summary>
    public int RunningCount
    {
        get
        {
            lock (_gate)
                return _jobs.Values.Count(j => j.State == WizardJobState.Running);
        }
    }

    /// <summary>
    /// Starts a loading for this account, or says why not. Nothing is sent before the answer: the loading runs in the
    /// background and its progress is read with <see cref="Find"/>.
    /// </summary>
    public async Task<WizardStart> StartAsync(string userId, WizardRequest request, CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            Purge(now);
            if (RunningJob(userId) is { } running)
                return new WizardStart(WizardStartOutcome.AlreadyRunning, running.Snapshot());

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await settings.RefreshAsync(db, cancellationToken).ConfigureAwait(false);
            if (WizardRequests.Check(request, CinemaCityCache.PolishToday(now), settings.Limits.MaxScreeningsPerProfile) is { } problem)
                return new WizardStart(WizardStartOutcome.Invalid, Problem: problem);

            var status = await UpstreamBlocks.ReadAsync(db, cancellationToken).ConfigureAwait(false);
            if (status.IsPausedByOperator)
                return new WizardStart(WizardStartOutcome.Refused, Reason: WizardStopReason.PausedByOperator);
            if (status.IsBlockedAt(now))
                return new WizardStart(WizardStartOutcome.Refused, Reason: WizardStopReason.Blocked, RetryAt: status.BlockedUntil);

            var items = Items(request);
            var estimate = await EstimateAsync(db, items, now, cancellationToken).ConfigureAwait(false);
            var account = accounts.For(userId);
            var tokens = new LoadingTokens(budget.Bucket, account);
            if (!tokens.TryReserve(estimate))
            {
                var accountWait = account.WaitFor(estimate);
                var wizardWait = budget.Bucket.WaitFor(estimate);
                if (accountWait is null || wizardWait is null)
                {
                    logger.LogWarning("Wizard loading refused: {Estimate} request(s) exceed the limits ({Burst} saved up, {PerAccount} per account)",
                        estimate, budget.Bucket.Capacity, account.Capacity);
                    return new WizardStart(WizardStartOutcome.Refused, Reason: WizardStopReason.TooLarge);
                }
                var retryAt = now + (accountWait > wizardWait ? accountWait.Value : wizardWait.Value);
                if (accountWait > TimeSpan.Zero)
                {
                    logger.LogInformation("Wizard loading refused: an account's limit is used up ({Estimate} request(s) needed)", estimate);
                    return new WizardStart(WizardStartOutcome.Refused, Reason: WizardStopReason.AccountLimit, RetryAt: retryAt);
                }
                var message = $"Wizard budget used up ({budget.Bucket.PerHour}/h, {budget.Bucket.Capacity} saved up): a loading of {estimate} request(s) was refused";
                events.Add(db, ServiceEventKind.WizardBudgetExhausted, message, throttled: true);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                logger.LogWarning("{Message}; enough again at {RetryAt:O}", message, retryAt);
                return new WizardStart(WizardStartOutcome.Refused, Reason: WizardStopReason.BudgetUsedUp, RetryAt: retryAt);
            }

            var job = new Job(Guid.CreateVersion7(now), userId, request, items, now, estimate);
            lock (_gate)
                _jobs[job.Id] = job;
            job.Completion = Task.Run(() => RunAsync(job, tokens), CancellationToken.None);
            return new WizardStart(WizardStartOutcome.Started, job.Snapshot());
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>The loading with this id if it belongs to this account (another account's id behaves as unknown).</summary>
    public WizardJobView? Find(Guid id, string userId)
    {
        Purge(time.GetUtcNow());
        lock (_gate)
            return _jobs.TryGetValue(id, out var job) && job.UserId == userId ? job.Snapshot() : null;
    }

    /// <summary>This account's running loading, if any.</summary>
    public WizardJobView? Running(string userId) => RunningJob(userId)?.Snapshot();

    /// <summary>
    /// What the hall map of these screenings can show without a request (step 3 of the site's wizard): the details
    /// from the cache or the poller's stored row, the layout from <c>seat_plans</c>, the free seats from the cache or
    /// the poller's result while young enough (otherwise null: occupancy unknown). A screening without details or
    /// layout carries the error "missing" (it needs a loading).
    /// </summary>
    public async Task<IReadOnlyList<ScreeningResult>> PeekScreeningsAsync(IReadOnlyList<string> screeningIds, CancellationToken cancellationToken = default)
    {
        var options = settings.Poller.Wizard;
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.WatchedScreenings.AsNoTracking().Where(r => screeningIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var plans = new Dictionary<(int, int), SeatPlan?>();
        var results = new List<ScreeningResult>(screeningIds.Count);
        foreach (var id in screeningIds)
        {
            var row = rows.GetValueOrDefault(id);
            var details = cache.TryPeek<Presentation>(CinemaCityCache.DetailsKey(id), out var cached) ? cached : RowDetails(row, now, options);
            SeatPlan? plan = null;
            if (details is not null)
            {
                var layout = (details.VenueId, details.SeatplanId);
                if (!plans.TryGetValue(layout, out plan))
                    plans[layout] = plan = (await SeatPlans.FindAsync(db, details.VenueId, details.SeatplanId, cancellationToken).ConfigureAwait(false))?.Plan;
            }
            var free = cache.TryPeek<FreeSeats>(CinemaCityCache.FreeSeatsKey(id), out var fresh) ? fresh : RowFreeSeats(row, now, options);
            results.Add(details is null || plan is null
                ? new ScreeningResult(id, details, plan, null, null, "missing")
                : new ScreeningResult(id, details, plan, free?.Keys, free?.At, null));
        }
        return results;
    }

    /// <summary>Waits until the loading has ended (tests; the page polls <see cref="Find"/> instead).</summary>
    public Task WhenEndedAsync(Guid id)
    {
        lock (_gate)
            return _jobs.TryGetValue(id, out var job) ? job.Completion : Task.CompletedTask;
    }

    /// <summary>The items of a request in the order they are loaded (days first, then the cinemas in city order).</summary>
    public static IReadOnlyList<WizardItem> Items(WizardRequest request) => request switch
    {
        RepertoireRequest r => r.Dates
            .SelectMany(date => Locations.ImaxCinemas.Where(c => r.CinemaIds.Contains(c.Id, StringComparer.Ordinal))
                .Select(c => new WizardItem(CinemaCityCache.RepertoireKey(c.Id, date), CinemaId: c.Id, Date: date)))
            .ToList(),
        ScreeningsRequest s => s.ScreeningIds.Select(id => new WizardItem("screening:" + id, ScreeningId: id)).ToList(),
        _ => throw new ArgumentOutOfRangeException(nameof(request)),
    };

    /// <summary>
    /// Requests the items will most likely need: what neither the cache nor the poller's stored results answer. A
    /// screening whose details are unknown may need its hall layout too; the six IMAX cinemas have one IMAX hall each,
    /// so at most six layouts are counted for them. Too high is fine (the rest goes back), too low is fine too (the
    /// extra requests take tokens directly).
    /// </summary>
    private async Task<int> EstimateAsync(AppDbContext db, IReadOnlyList<WizardItem> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var options = settings.Poller.Wizard;
        var requests = items.Count(i => i.CinemaId is not null && i.Date is not null && !cache.TryPeek<DayRepertoire>(i.Key, out _));
        var ids = items.Where(i => i.ScreeningId is not null).Select(i => i.ScreeningId!).ToList();
        if (ids.Count == 0)
            return requests;

        var rows = await db.WatchedScreenings.AsNoTracking().Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var plans = new HashSet<(int, int)>();
        var unknownHalls = 0;
        foreach (var id in ids)
        {
            var row = rows.GetValueOrDefault(id);
            var details = cache.TryPeek<Presentation>(CinemaCityCache.DetailsKey(id), out var cached) ? cached : RowDetails(row, now, options);
            if (details is null)
            {
                requests++;
                unknownHalls++;
            }
            else
                plans.Add((details.VenueId, details.SeatplanId));
            if (RowFreeSeats(row, now, options) is null && !cache.TryPeek<FreeSeats>(CinemaCityCache.FreeSeatsKey(id), out _))
                requests++;
        }
        foreach (var (venueId, seatplanId) in plans)
        {
            var fetchedAt = await db.SeatPlans.AsNoTracking().Where(p => p.VenueId == venueId && p.SeatplanId == seatplanId)
                .Select(p => (DateTimeOffset?)p.FetchedAt).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (fetchedAt is not { } at || now - at >= PollerCycle.SeatPlanMaxAge)
                requests++;
        }
        return requests + Math.Min(unknownHalls, Locations.ImaxCinemas.Count);
    }

    private async Task RunAsync(Job job, LoadingTokens tokens)
    {
        var stopping = lifetime.ApplicationStopping;
        var cycle = "wizard-" + job.Id.ToString("N")[^8..];
        var flushed = 0;
        using var guard = new RequestGuard(network.CreateHandler(), pacer, tokens, time, RequestSource.Wizard);
        using var client = new CinemaCityClient(guard, disposeHandler: false, PollerInfo.UserAgent);
        var loading = new Loading(this, client);
        try
        {
            foreach (var item in job.Items)
            {
                if (stopping.IsCancellationRequested)
                {
                    job.Stop(WizardStopReason.ServerStopping, null);
                    break;
                }
                var before = guard.Records.Count;
                guard.PaceKey = item.Key;
                guard.CurrentScreeningId = item.ScreeningId;
                try
                {
                    if (item.ScreeningId is { } id)
                        job.Add(await loading.ScreeningAsync(id, stopping).ConfigureAwait(false), guard.Records.Count - before);
                    else
                        job.Add(await loading.RepertoireAsync(item.CinemaId!, item.Date!.Value, stopping).ConfigureAwait(false), guard.Records.Count - before);
                }
                catch (WizardStoppedException stop)
                {
                    job.Stop(stop.Reason, stop.RetryAt);
                    break;
                }
                catch (Exception) when (guard.BlockedBy is { } blocked)
                {
                    job.Stop(WizardStopReason.Blocked, await RecordBlockAsync(guard, blocked).ConfigureAwait(false));
                    break;
                }
                catch (BudgetExhaustedException)
                {
                    var reason = tokens.Shortage ?? WizardStopReason.BudgetUsedUp;
                    var wait = (reason == WizardStopReason.AccountLimit ? accounts.For(job.UserId) : budget.Bucket).WaitFor(1);
                    job.Stop(reason, wait is { } w ? time.GetUtcNow() + w : null);
                    break;
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    job.Stop(WizardStopReason.ServerStopping, null);
                    break;
                }
                catch (CinemaCityException ex) when ((int)ex.StatusCode is 403 or 429)
                {
                    // Another loading's request for the same item was refused: that loading records the block.
                    job.Stop(WizardStopReason.Blocked, null);
                    break;
                }
                catch (Exception ex)
                {
                    var error = PollerCycle.Describe(ex);
                    logger.LogWarning("Wizard loading {Cycle}: {Item} failed ({Error})", cycle, item.Key, error);
                    if (item.ScreeningId is { } id)
                        job.Add(new ScreeningResult(id, null, null, null, null, error), guard.Records.Count - before, failed: true);
                    else
                        job.Add(new RepertoireResult(item.CinemaId!, item.Date!.Value, null, error), guard.Records.Count - before, failed: true);
                }
                finally
                {
                    job.SetRequests(guard.Records.Count); // a stopped item's request counts too
                    flushed = await FlushLogAsync(guard, flushed, cycle).ConfigureAwait(false);
                }
            }
            job.Finish(time.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wizard loading {Cycle} failed", cycle);
            job.Stop(WizardStopReason.ServerStopping, null);
            job.Finish(time.GetUtcNow());
        }
        finally
        {
            tokens.Release();
        }
        var view = job.Snapshot();
        logger.LogInformation("Wizard loading {Cycle}: {State}{Reason}, {Done}/{Total} item(s), {FromCache} from the cache, {Failed} failed, {Requests} request(s) (estimated {Estimate})",
            cycle, view.State, view.StopReason is { } r ? $" ({r})" : "", view.Done, view.Total, view.FromCache, view.Failed, view.Requests, job.Estimate);
    }

    /// <summary>Writes the requests sent since the last call to check_log (after every item, so the panel is current).</summary>
    private async Task<int> FlushLogAsync(RequestGuard guard, int flushed, string cycle)
    {
        var records = guard.Records;
        if (records.Count == flushed)
            return flushed;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var r in records.Skip(flushed))
            {
                db.CheckLog.Add(new CheckLogEntry
                {
                    At = r.At,
                    Cycle = cycle,
                    Source = r.Source,
                    ScreeningId = r.ScreeningId,
                    Manual = false,
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
                });
            }
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogError(ex, "Wizard loading {Cycle}: the request log could not be written", cycle);
        }
        return records.Count;
    }

    /// <summary>A block the wizard ran into: the same pause, event and alert as the poller's (unless one is already in force).</summary>
    private async Task<DateTimeOffset?> RecordBlockAsync(RequestGuard guard, RequestRecord blocked)
    {
        await blocks.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = time.GetUtcNow();
            var state = await db.PollerState.SingleOrDefaultAsync(s => s.Id == PollerStateRow.SingleId).ConfigureAwait(false);
            if (state is null)
            {
                state = new PollerStateRow();
                db.PollerState.Add(state);
            }
            if (state.BlockedUntil is { } until && until > now)
                return until; // somebody recorded it a moment ago: one block, one pause
            await settings.RefreshAsync(db).ConfigureAwait(false);
            var end = await blocks.RecordBlockAsync(db, state, guard, blocked, now, settings.Poller, CancellationToken.None).ConfigureAwait(false);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return end;
        }
        finally
        {
            blocks.Gate.Release();
        }
    }

    private Job? RunningJob(string userId)
    {
        lock (_gate)
            return _jobs.Values.FirstOrDefault(j => j.UserId == userId && j.State == WizardJobState.Running);
    }

    private void Purge(DateTimeOffset now)
    {
        var keep = TimeSpan.FromMinutes(settings.Poller.Wizard.KeepResultMinutes);
        lock (_gate)
        {
            foreach (var job in _jobs.Values.Where(j => j.FinishedAt is { } at && now - at > keep).ToList())
                _jobs.Remove(job.Id);
        }
    }

    /// <summary>The details stored by the poller, when fresh enough (hall and layout; no request).</summary>
    private static Presentation? RowDetails(WatchedScreeningRow? row, DateTimeOffset now, WizardOptions options) =>
        row is { VenueId: { } venue, SeatplanId: { } seatplan, VenueTypeId: { } venueType, IsReserved: { } reserved, DetailsFetchedAt: { } fetched }
        && now - fetched < TimeSpan.FromMinutes(options.DetailsMinutes)
            ? new Presentation(row.Id, "", default, "", venue, seatplan, venueType, reserved, false, null)
            : null;

    /// <summary>The poller's last result, when young enough for the hall map (no request).</summary>
    private static FreeSeats? RowFreeSeats(WatchedScreeningRow? row, DateTimeOffset now, WizardOptions options) =>
        row is { FreeSeatKeys: { } keys, LastSucceededAt: { } at } && now - at < TimeSpan.FromMinutes(options.FreeSeatsMinutes)
            ? new FreeSeats(new HashSet<string>(keys, StringComparer.Ordinal), at)
            : null;

    /// <summary>One loading's way to the data: the cache first, then Cinema City through the loading's own client.</summary>
    private sealed class Loading(WizardJobs owner, CinemaCityClient client)
    {
        private bool _recoveryChecked;

        public async Task<RepertoireResult> RepertoireAsync(string cinemaId, DateOnly date, CancellationToken cancellationToken)
        {
            var options = owner.Settings.Poller.Wizard;
            var day = await owner.Cache.GetAsync(CinemaCityCache.RepertoireKey(cinemaId, date),
                _ => CinemaCityCache.RepertoireKeep(date, owner.Time.GetUtcNow(), options),
                () => SendAsync(c => c.GetRepertoireAsync(cinemaId, date, cancellationToken), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new RepertoireResult(cinemaId, date, day, null);
        }

        public async Task<ScreeningResult> ScreeningAsync(string screeningId, CancellationToken cancellationToken)
        {
            var options = owner.Settings.Poller.Wizard;
            var now = owner.Time.GetUtcNow();
            WatchedScreeningRow? row;
            await using (var scope = owner.Scopes.CreateAsyncScope())
            {
                row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().WatchedScreenings.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.Id == screeningId, cancellationToken).ConfigureAwait(false);
            }

            var details = owner.Cache.TryPeek<Presentation>(CinemaCityCache.DetailsKey(screeningId), out var cached) ? cached
                : RowDetails(row, now, options)
                  ?? await owner.Cache.GetAsync(CinemaCityCache.DetailsKey(screeningId),
                      _ => TimeSpan.FromMinutes(options.DetailsMinutes),
                      () => SendAsync(c => c.GetPresentationAsync(screeningId, cancellationToken), cancellationToken),
                      cancellationToken).ConfigureAwait(false);
            var plan = await SeatPlanAsync(details.VenueId, details.SeatplanId, cancellationToken).ConfigureAwait(false);
            var free = RowFreeSeats(row, now, options)
                       ?? await owner.Cache.GetAsync(CinemaCityCache.FreeSeatsKey(screeningId),
                           _ => TimeSpan.FromMinutes(options.FreeSeatsMinutes),
                           async () => new FreeSeats(
                               await SendAsync(c => c.GetFreeSeatsAsync(screeningId, details.VenueTypeId, details.IsReserved, cancellationToken), cancellationToken).ConfigureAwait(false),
                               owner.Time.GetUtcNow()),
                           cancellationToken).ConfigureAwait(false);
            return new ScreeningResult(screeningId, details, plan, free.Keys, free.At, null);
        }

        /// <summary>The layout from seat_plans (shared with the poller), fetched and stored when missing or old.</summary>
        private Task<SeatPlan> SeatPlanAsync(int venueId, int seatplanId, CancellationToken cancellationToken) =>
            owner.Cache.GetAsync(CinemaCityCache.SeatPlanKey(venueId, seatplanId), _ => TimeSpan.Zero, async () =>
            {
                await using var scope = owner.Scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await SeatPlans.FindAsync(db, venueId, seatplanId, cancellationToken).ConfigureAwait(false) is { } stored
                    && owner.Time.GetUtcNow() - stored.FetchedAt < PollerCycle.SeatPlanMaxAge)
                {
                    return stored.Plan;
                }
                var json = await SendAsync(c => c.GetSeatPlanJsonAsync(venueId, seatplanId, cancellationToken), cancellationToken).ConfigureAwait(false);
                var plan = await SeatPlans.StoreAsync(db, venueId, seatplanId, json, owner.Time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    // The poller stored the same layout at the same moment: theirs is as good as ours.
                }
                return plan;
            }, cancellationToken);

        /// <summary>
        /// One request to Cinema City: only when neither the owner's pause nor a block is in force (read before every
        /// request, so a pause or a block elsewhere stops the loading at once). The first request that gets through
        /// after a block's pause ends the block.
        /// </summary>
        private async Task<T> SendAsync<T>(Func<CinemaCityClient, Task<T>> call, CancellationToken cancellationToken)
        {
            UpstreamStatus status;
            await using (var scope = owner.Scopes.CreateAsyncScope())
                status = await UpstreamBlocks.ReadAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken).ConfigureAwait(false);
            var now = owner.Time.GetUtcNow();
            if (status.IsPausedByOperator)
                throw new WizardStoppedException(WizardStopReason.PausedByOperator, null);
            if (status.IsBlockedAt(now))
                throw new WizardStoppedException(WizardStopReason.Blocked, status.BlockedUntil);

            var result = await call(client).ConfigureAwait(false);
            if (!_recoveryChecked && status.IsRecoveringAt(now))
            {
                _recoveryChecked = true;
                await owner.Blocks.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await using var scope = owner.Scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    if (await owner.Blocks.TryResumeAsync(db, null, CancellationToken.None).ConfigureAwait(false))
                        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    owner.Blocks.Gate.Release();
                }
            }
            return result;
        }
    }

    /// <summary>One loading's state; changed by its background run, read as <see cref="WizardJobView"/> copies.</summary>
    private sealed class Job(Guid id, string userId, WizardRequest request, IReadOnlyList<WizardItem> items, DateTimeOffset startedAt, int estimate)
    {
        private readonly Lock _gate = new();
        private readonly List<RepertoireResult> _repertoire = [];
        private readonly List<ScreeningResult> _screenings = [];
        private WizardStopReason? _stopReason;
        private DateTimeOffset? _retryAt;
        private int _done;
        private int _fromCache;
        private int _failed;
        private int _requests;

        public Guid Id { get; } = id;

        public string UserId { get; } = userId;

        public IReadOnlyList<WizardItem> Items { get; } = items;

        public int Estimate { get; } = estimate;

        public Task Completion { get; set; } = Task.CompletedTask;

        public WizardJobState State { get; private set; } = WizardJobState.Running;

        public DateTimeOffset? FinishedAt { get; private set; }

        public void Add(RepertoireResult result, int requests, bool failed = false)
        {
            lock (_gate)
            {
                _repertoire.Add(result);
                Count(requests, failed);
            }
        }

        public void Add(ScreeningResult result, int requests, bool failed = false)
        {
            lock (_gate)
            {
                _screenings.Add(result);
                Count(requests, failed);
            }
        }

        public void SetRequests(int requests)
        {
            lock (_gate)
                _requests = requests;
        }

        public void Stop(WizardStopReason reason, DateTimeOffset? retryAt)
        {
            lock (_gate)
            {
                _stopReason ??= reason;
                _retryAt ??= retryAt;
            }
        }

        public void Finish(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (State != WizardJobState.Running)
                    return;
                FinishedAt = now;
                State = _stopReason is null ? WizardJobState.Finished : WizardJobState.Stopped;
            }
        }

        public WizardJobView Snapshot()
        {
            lock (_gate)
            {
                return new WizardJobView(Id, request, State, _stopReason, _retryAt, startedAt, FinishedAt, Items.Count, _done, _fromCache, _failed,
                    _requests, _repertoire.ToList(), _screenings.ToList());
            }
        }

        private void Count(int requests, bool failed)
        {
            _done++;
            if (requests == 0 && !failed)
                _fromCache++;
            if (failed)
                _failed++;
        }
    }
}
