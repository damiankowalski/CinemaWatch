using System.Data;
using System.Data.Common;
using System.Diagnostics;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using CinemaWatch.Server.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Operations;

/// <summary>How a part of the service is doing, as the panel's checklist shows it.</summary>
public enum HealthLevel
{
    Ok,

    /// <summary>Works, but needs a look (paused by the owner, a failed last request, no ping yet).</summary>
    Warning,

    /// <summary>Does not work (a block, no cycle for minutes, the last e-mail or ping failed).</summary>
    Problem,

    /// <summary>Switched off or not used yet.</summary>
    Off,
}

public sealed record PollerHealth(
    HealthLevel Level,
    bool Enabled,
    CinemaCitySource Source,
    DateTimeOffset? LastCycleAt,
    DateTimeOffset? PausedByOperatorAt,
    DateTimeOffset? BlockedUntil,
    int BlockCount,
    DateTimeOffset? BlockedAt,
    string? BlockReason,
    int WatchedScreenings,
    int DueNow,
    DateTimeOffset? NextCheckAt);

public sealed record CinemaCityHealth(HealthLevel Level, CheckLogEntry? LastRequest, DateTimeOffset? LastOkAt, ServiceEvent? LastBlock);

/// <summary>The mail server or the heartbeat: last success and last failure (component_status).</summary>
public sealed record ComponentHealth(HealthLevel Level, DateTimeOffset? LastOkAt, DateTimeOffset? LastFailureAt, string? LastFailure);

public sealed record TableSize(string Name, long Bytes, long Rows);

public sealed record DatabaseHealth(HealthLevel Level, string? Version, long? SizeBytes, string? LastMigration, IReadOnlyList<TableSize> Tables, string? Error);

/// <summary>Requests to Cinema City in the last hour against the hourly budget; tokens in the bucket now.</summary>
public sealed record BudgetUse(int LastHour, int PerHour, int Available, int Capacity);

/// <summary>
/// The wizard's data on demand: its requests in the last hour against its own budget, the limit per account, how many
/// accounts used part of it recently, loadings running now and what the shared cache did since the start.
/// </summary>
public sealed record WizardUse(BudgetUse Budget, int AccountPerHour, int AccountsUsingLimit, int Running, CacheStats Cache);

public sealed record OperatorOverview(
    DateTimeOffset Now,
    DateTimeOffset StartedAt,
    PollerHealth Poller,
    CinemaCityHealth CinemaCity,
    ComponentHealth Mail,
    ComponentHealth Heartbeat,
    HeartbeatOptions HeartbeatSettings,
    DatabaseHealth Database,
    BudgetUse Budget,
    WizardUse Wizard);

/// <summary>
/// One hour of requests (UTC hour; Polish time differs by whole hours, so the buckets fit both); <see cref="Wizard"/>
/// of them came from the wizard, the rest from the poller.
/// </summary>
public sealed record HourStats(DateTimeOffset Hour, int Requests, int Ok, int Failed, int Blocked, int Manual, int Wizard);

/// <summary>Requests per HTTP status; null = no response (timeout, network error).</summary>
public sealed record StatusCount(int? Status, int Last24Hours, int Last7Days);

public sealed record RequestStats(
    IReadOnlyList<HourStats> Hours,
    IReadOnlyList<StatusCount> Statuses,
    int Total24Hours,
    int Total7Days,
    int Blocked24Hours,
    int Blocked7Days,
    long? AverageMs24Hours,
    IReadOnlyList<CheckLogEntry> Recent);

public sealed record SettingsSaveResult(IReadOnlyList<SettingChange> Changes);

/// <summary>
/// The operator's panel (owner only; docs/SAAS.md §5.1): what the checklist, the statistics and the event list show,
/// and the three things the owner can do there - pause / resume all requests (the poller's and the wizard's) and change
/// the poller and wizard settings within their fixed ranges. Every action is recorded in service_events. Makes no
/// Cinema City requests.
/// </summary>
public sealed class OperatorService(
    AppDbContext db,
    PollerSettings settings,
    RequestBudget budget,
    WizardBudget wizardBudget,
    AccountBudgets accountBudgets,
    CinemaCityCache cache,
    WizardJobs wizardJobs,
    PollerSignal signal,
    ServiceEvents events,
    IOptions<PollerOptions> pollerOptions,
    IOptions<HeartbeatOptions> heartbeatOptions,
    TimeProvider time,
    ILogger<OperatorService> logger)
{
    /// <summary>The poller runs a cycle every 30 s; none for this long means it is stuck or not running.</summary>
    public static readonly TimeSpan StaleCycle = TimeSpan.FromMinutes(5);

    public const int RecentRequestsShown = 20;
    public const int EventsShown = 100;

    public async Task<OperatorOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await settings.RefreshAsync(db, cancellationToken);
        var state = await db.PollerState.AsNoTracking().SingleOrDefaultAsync(s => s.Id == PollerStateRow.SingleId, cancellationToken);

        var watched = await db.WatchedScreenings.CountAsync(cancellationToken);
        var due = await db.WatchedScreenings.CountAsync(r => r.NextCheckAt <= now || r.ManualRequestedAt != null, cancellationToken);
        var next = await db.WatchedScreenings.MinAsync(r => (DateTimeOffset?)r.NextCheckAt, cancellationToken);
        var enabled = pollerOptions.Value.Enabled;
        var blockedNow = state?.BlockedUntil is { } until && until > now;
        var pollerLevel =
            !enabled ? HealthLevel.Off
            : blockedNow ? HealthLevel.Problem
            : state?.LastCycleAt is not { } lastCycle ? HealthLevel.Warning
            : now - lastCycle > StaleCycle ? HealthLevel.Problem
            : state.PausedByOperatorAt is not null ? HealthLevel.Warning
            : HealthLevel.Ok;
        var poller = new PollerHealth(pollerLevel, enabled, pollerOptions.Value.Source, state?.LastCycleAt, state?.PausedByOperatorAt,
            blockedNow ? state!.BlockedUntil : null, state?.BlockCount ?? 0, state?.BlockedAt, state?.BlockReason, watched, due, next);

        var lastRequest = await db.CheckLog.AsNoTracking().OrderByDescending(e => e.At).ThenByDescending(e => e.Id).FirstOrDefaultAsync(cancellationToken);
        var lastOk = await db.CheckLog.Where(e => e.Status >= 200 && e.Status < 300 && e.CfMitigated == null && e.ContentType != "text/html")
            .MaxAsync(e => (DateTimeOffset?)e.At, cancellationToken);
        var lastBlock = await db.ServiceEvents.AsNoTracking().Where(e => e.Kind == ServiceEventKind.Blocked)
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id).FirstOrDefaultAsync(cancellationToken);
        var cinemaCityLevel =
            blockedNow ? HealthLevel.Problem
            : lastRequest is null ? HealthLevel.Off
            : IsSuccess(lastRequest) ? HealthLevel.Ok
            : HealthLevel.Warning;

        var statuses = await db.ComponentStatus.AsNoTracking().ToDictionaryAsync(s => s.Component, StringComparer.Ordinal, cancellationToken);
        var mail = Health(statuses.GetValueOrDefault(ComponentStatusRow.Mail), configured: true, unused: HealthLevel.Off);
        var heartbeatSettings = heartbeatOptions.Value;
        // A configured heartbeat that has not pinged yet needs a look: the external service is waiting for it.
        var heartbeat = Health(statuses.GetValueOrDefault(ComponentStatusRow.Heartbeat), heartbeatSettings.IsEnabled, unused: HealthLevel.Warning);

        var hourAgo = now - TimeSpan.FromHours(1);
        var lastHour = await db.CheckLog.CountAsync(e => e.At > hourAgo && e.Source == RequestSource.Poller, cancellationToken);
        var use = new BudgetUse(lastHour, settings.Poller.RequestsPerHour, budget.Available, budget.Capacity);
        var wizardLastHour = await db.CheckLog.CountAsync(e => e.At > hourAgo && e.Source == RequestSource.Wizard, cancellationToken);
        var wizardBucket = wizardBudget.Bucket;
        var wizard = new WizardUse(new BudgetUse(wizardLastHour, wizardBucket.PerHour, wizardBucket.Available, wizardBucket.Capacity),
            settings.Poller.Wizard.AccountRequestsPerHour, accountBudgets.ActiveAccounts, wizardJobs.RunningCount, cache.Stats);

        return new OperatorOverview(now, ProcessStart(), poller, new CinemaCityHealth(cinemaCityLevel, lastRequest, lastOk, lastBlock),
            mail, heartbeat, heartbeatSettings, await DatabaseAsync(cancellationToken), use, wizard);
    }

    /// <summary>
    /// Requests of the last 24 hours hour by hour (the current hour included) and per status for 24 hours and 7 days,
    /// from check_log. At most a few thousand rows a day, so they are counted here rather than in SQL.
    /// </summary>
    public async Task<RequestStats> GetRequestStatsAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var weekAgo = now - TimeSpan.FromDays(7);
        var dayAgo = now - TimeSpan.FromHours(24);
        var rows = await db.CheckLog.AsNoTracking()
            .Where(e => e.At > weekAgo)
            .Select(e => new { e.At, e.Status, e.Manual, e.CfMitigated, e.ContentType, e.DurationMs, e.Source })
            .ToListAsync(cancellationToken);

        var currentHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var hours = new List<HourStats>();
        for (var i = 0; i < 24; i++)
        {
            var start = currentHour - TimeSpan.FromHours(i);
            var end = start + TimeSpan.FromHours(1);
            var bucket = rows.Where(r => r.At >= start && r.At < end).ToList();
            var blocked = bucket.Count(r => r.Status is { } status && RequestGuard.IsBlock(status, r.CfMitigated, r.ContentType));
            var ok = bucket.Count(r => r.Status is >= 200 and < 300 && !RequestGuard.IsBlock(r.Status.Value, r.CfMitigated, r.ContentType));
            hours.Add(new HourStats(start, bucket.Count, ok, bucket.Count - ok - blocked, blocked, bucket.Count(r => r.Manual),
                bucket.Count(r => r.Source == RequestSource.Wizard)));
        }

        var day = rows.Where(r => r.At > dayAgo).ToList();
        var statuses = rows.GroupBy(r => r.Status)
            .Select(g => new StatusCount(g.Key, g.Count(r => r.At > dayAgo), g.Count()))
            .OrderBy(s => s.Status is null)
            .ThenBy(s => s.Status)
            .ToList();
        bool IsBlocked(int? status, string? cfMitigated, string? contentType) => status is { } code && RequestGuard.IsBlock(code, cfMitigated, contentType);

        var recent = await db.CheckLog.AsNoTracking()
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Take(RecentRequestsShown)
            .ToListAsync(cancellationToken);

        return new RequestStats(hours, statuses, day.Count, rows.Count,
            day.Count(r => IsBlocked(r.Status, r.CfMitigated, r.ContentType)), rows.Count(r => IsBlocked(r.Status, r.CfMitigated, r.ContentType)),
            day.Count == 0 ? null : (long)Math.Round(day.Average(r => r.DurationMs)), recent);
    }

    public async Task<IReadOnlyList<ServiceEvent>> GetEventsAsync(CancellationToken cancellationToken = default) =>
        await db.ServiceEvents.AsNoTracking()
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Take(EventsShown)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Pauses all checks (scheduled and "Check now") from the next cycle until <see cref="ResumeAsync"/>. Survives a
    /// restart. False when they were already paused.
    /// </summary>
    public async Task<bool> PauseAsync(CancellationToken cancellationToken = default)
    {
        var state = await db.PollerState.SingleOrDefaultAsync(s => s.Id == PollerStateRow.SingleId, cancellationToken);
        if (state?.PausedByOperatorAt is not null)
            return false;
        if (state is null)
        {
            state = new PollerStateRow();
            db.PollerState.Add(state);
        }
        state.PausedByOperatorAt = time.GetUtcNow();
        events.Add(db, ServiceEventKind.PausedByOperator, "Checks paused by the operator: no requests to Cinema City (poller and wizard) until they are resumed");
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Checks paused by the operator");
        return true;
    }

    /// <summary>Resumes the checks paused by <see cref="PauseAsync"/> (a block, if one is in force, still applies). False when they were not paused.</summary>
    public async Task<bool> ResumeAsync(CancellationToken cancellationToken = default)
    {
        var state = await db.PollerState.SingleOrDefaultAsync(s => s.Id == PollerStateRow.SingleId, cancellationToken);
        if (state?.PausedByOperatorAt is not { } since)
            return false;
        state.PausedByOperatorAt = null;
        events.Add(db, ServiceEventKind.ResumedByOperator, $"Checks resumed by the operator (paused since {since:O})");
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Checks resumed by the operator");
        signal.Wake();
        return true;
    }

    /// <summary>
    /// Stores the overrides from <see cref="PollerSettings.Parse"/> (an empty set removes them all: the configuration
    /// applies again). Applies at once in this process and from the next cycle anywhere; recorded as an event.
    /// </summary>
    public async Task<SettingsSaveResult> SaveSettingsAsync(PollerSettingsRow overrides, CancellationToken cancellationToken = default)
    {
        await settings.RefreshAsync(db, cancellationToken);
        var changes = settings.ChangesTo(overrides);
        if (changes.Count == 0)
            return new SettingsSaveResult([]);

        var row = await db.PollerSettings.SingleOrDefaultAsync(s => s.Id == PollerSettingsRow.SingleId, cancellationToken);
        if (overrides.IsEmpty)
        {
            if (row is not null)
                db.PollerSettings.Remove(row);
        }
        else
        {
            if (row is null)
            {
                row = new PollerSettingsRow();
                db.PollerSettings.Add(row);
            }
            foreach (var setting in PollerTuning.Numbers)
                setting.Write(row, setting.Read(overrides));
            row.ReuseScreeningDetails = overrides.ReuseScreeningDetails;
            row.UpdatedAt = time.GetUtcNow();
        }
        var description = PollerTuning.Describe(changes);
        events.Add(db, ServiceEventKind.SettingsChanged, $"Poller settings changed by the operator: {description}");
        await db.SaveChangesAsync(cancellationToken);
        settings.Apply(overrides.IsEmpty ? null : overrides);
        logger.LogInformation("Poller settings changed by the operator: {Changes}", description);
        return new SettingsSaveResult(changes);
    }

    private static bool IsSuccess(CheckLogEntry entry) =>
        entry.Status is >= 200 and < 300 && !RequestGuard.IsBlock(entry.Status.Value, entry.CfMitigated, entry.ContentType);

    private static ComponentHealth Health(ComponentStatusRow? row, bool configured, HealthLevel unused)
    {
        if (!configured)
            return new ComponentHealth(HealthLevel.Off, row?.LastOkAt, row?.LastFailureAt, row?.LastFailure);
        if (row is null || row.LastOkAt is null && row.LastFailureAt is null)
            return new ComponentHealth(unused, null, null, null);
        var failedLast = row.LastFailureAt is { } failed && (row.LastOkAt is not { } ok || failed > ok);
        return new ComponentHealth(failedLast ? HealthLevel.Problem : HealthLevel.Ok, row.LastOkAt, row.LastFailureAt, row.LastFailure);
    }

    /// <summary>Version, size, the largest tables and the last migration; a failure is shown, not thrown.</summary>
    private async Task<DatabaseHealth> DatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var lastMigration = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).LastOrDefault();
            var connection = db.Database.GetDbConnection();
            var opened = connection.State != ConnectionState.Open;
            if (opened)
                await connection.OpenAsync(cancellationToken);
            try
            {
                string? version = null;
                long? size = null;
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select current_setting('server_version'), pg_database_size(current_database())";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        version = reader.GetString(0);
                        size = reader.GetInt64(1);
                    }
                }
                var largest = new List<(string Name, long Bytes)>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        select relname, pg_total_relation_size(relid)
                        from pg_stat_user_tables
                        order by pg_total_relation_size(relid) desc, relname
                        limit 8
                        """;
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        largest.Add((reader.GetString(0), reader.GetInt64(1)));
                }
                // Exact counts: PostgreSQL's own statistics lag behind, and these tables hold thousands of rows, not millions.
                var tables = new List<TableSize>();
                foreach (var (name, bytes) in largest)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"select count(*) from \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
                    tables.Add(new TableSize(name, bytes, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)));
                }
                return new DatabaseHealth(HealthLevel.Ok, version, size, lastMigration, tables, null);
            }
            finally
            {
                if (opened)
                    await connection.CloseAsync();
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            logger.LogError(ex, "Database details for the operator's panel could not be read");
            return new DatabaseHealth(HealthLevel.Problem, null, null, null, [], ex.Message);
        }
    }

    private static DateTimeOffset ProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }
}
