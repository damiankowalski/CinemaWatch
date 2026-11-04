using CinemaWatch.Core;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CinemaWatch.Server.Polling;

/// <summary>A profile watching a screening: who gets the result of that screening's check.</summary>
public sealed record Watcher(StoredProfile Profile, WatchProfile Document, WatchedScreening Screening);

/// <summary>
/// Keeps watched_screenings in step with the profiles at the start of every cycle: one row per upcoming screening of
/// an enabled, unfinished profile; new screenings become due within a minute, screenings nobody watches any more (or
/// that have started) disappear. Also turns "Check now" into due screenings and drops notification state of
/// screenings that are over or no longer in their profile.
/// </summary>
public static class WatchList
{
    /// <summary>New screenings are checked within this time, spread so that an import of several does not make a burst.</summary>
    public static readonly TimeSpan NewScreeningSpread = TimeSpan.FromSeconds(60);

    public static async Task<IReadOnlyDictionary<string, List<Watcher>>> SyncAsync(
        AppDbContext db, LimitOptions limits, DateTimeOffset now, ILogger logger, CancellationToken cancellationToken)
    {
        var polishNow = PolishTime.FromUtc(now.UtcDateTime);
        var profiles = await db.Profiles.ToListAsync(cancellationToken);
        var documents = new Dictionary<Guid, WatchProfile>();
        foreach (var profile in profiles)
        {
            try
            {
                documents[profile.Id] = ProfileStore.Parse(profile.Document);
            }
            catch (Exception ex)
            {
                // Checked at import, so this means a bug or a manual change in the database.
                logger.LogError(ex, "Profile {ProfileId} cannot be read; it is not checked", profile.Id);
            }
        }

        var watchers = new Dictionary<string, List<Watcher>>(StringComparer.Ordinal);
        foreach (var profile in profiles.Where(p => p.Enabled && p.EndsAt > now))
        {
            if (!documents.TryGetValue(profile.Id, out var document))
                continue;
            foreach (var screening in document.Screenings.Where(s => s.StartsAt > polishNow))
            {
                if (!watchers.TryGetValue(screening.Id, out var list))
                    watchers[screening.Id] = list = [];
                list.Add(new Watcher(profile, document, screening));
            }
        }

        var rows = await db.WatchedScreenings.ToDictionaryAsync(r => r.Id, StringComparer.Ordinal, cancellationToken);
        foreach (var (id, list) in watchers)
        {
            var first = list[0];
            var interval = list.Min(w => limits.EffectiveInterval(w.Document.IntervalMinutes));
            var cinemaId = first.Screening.CinemaId.Length > 0 ? first.Screening.CinemaId : first.Document.CinemaId;
            var startsAt = PollerTime.ToUtc(first.Screening.StartsAt);
            if (rows.TryGetValue(id, out var row))
            {
                if (row.IntervalMinutes != interval)
                    row.IntervalMinutes = interval;
                if (row.CinemaId != cinemaId)
                    row.CinemaId = cinemaId;
                if (row.StartsAt != startsAt)
                    row.StartsAt = startsAt;
                continue;
            }
            row = new WatchedScreeningRow
            {
                Id = id,
                CinemaId = cinemaId,
                StartsAt = startsAt,
                IntervalMinutes = interval,
                NextCheckAt = now + TimeSpan.FromSeconds(Random.Shared.NextDouble() * NewScreeningSpread.TotalSeconds),
            };
            db.WatchedScreenings.Add(row);
            rows[id] = row;
        }
        foreach (var row in rows.Values.Where(r => !watchers.ContainsKey(r.Id)).ToList())
        {
            db.WatchedScreenings.Remove(row);
            rows.Remove(row.Id);
        }

        // "Check now": the profile's screenings are due at once (the cycle decides whether a request is needed).
        foreach (var profile in profiles.Where(p => p.CheckPending))
        {
            profile.CheckPending = false;
            if (!profile.Enabled || profile.EndsAt <= now || !documents.TryGetValue(profile.Id, out var document))
                continue;
            foreach (var screening in document.Screenings)
            {
                if (rows.TryGetValue(screening.Id, out var row))
                    row.ManualRequestedAt ??= now;
            }
        }

        // Notification state only for upcoming screenings that are still in their profile (enabled or not).
        var valid = new HashSet<(Guid, string)>();
        foreach (var (profileId, document) in documents)
        {
            foreach (var screening in document.Screenings.Where(s => s.StartsAt > polishNow))
                valid.Add((profileId, screening.Id));
        }
        var states = await db.NotificationStates.Select(s => new { s.ProfileId, s.ScreeningId }).ToListAsync(cancellationToken);
        foreach (var stale in states.Where(s => !valid.Contains((s.ProfileId, s.ScreeningId))))
        {
            var tracked = db.NotificationStates.Local.FirstOrDefault(s => s.ProfileId == stale.ProfileId && s.ScreeningId == stale.ScreeningId);
            db.NotificationStates.Remove(tracked ?? new NotificationStateRow { ProfileId = stale.ProfileId, ScreeningId = stale.ScreeningId });
        }

        await db.SaveChangesAsync(cancellationToken);
        return watchers;
    }
}
