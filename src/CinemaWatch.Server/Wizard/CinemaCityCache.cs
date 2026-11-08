using CinemaWatch.Core;
using CinemaWatch.Server.Polling;

namespace CinemaWatch.Server.Wizard;

/// <summary>What the cache did since the start (for the operator's panel).</summary>
public sealed record CacheStats(int Entries, long Hits, long Shared, long Loads);

/// <summary>
/// Cinema City data in the server's memory, shared by all accounts (docs/SAAS.md §5.2): the repertoire, screening
/// details and free seats are the same for everybody, so whoever asks first pays the request and everyone after gets
/// the answer from here. <b>Single flight</b>: identical loads running at the same time go out once - the second caller
/// waits for the first one's answer. A failure is never kept: the next caller tries again. Lost on restart (nothing to
/// migrate, nothing to back up).
/// </summary>
public sealed class CinemaCityCache(TimeProvider time)
{
    /// <summary>Above this many entries the expired ones are swept out at the next insert.</summary>
    public const int SweepAbove = 2000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _hits;
    private long _shared;
    private long _loads;

    private sealed class Entry(TaskCompletionSource<object> source)
    {
        public TaskCompletionSource<object> Source { get; } = source;

        /// <summary>Set (under the lock) once the value is there; until then the entry is a load in flight.</summary>
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MaxValue;
    }

    public CacheStats Stats
    {
        get
        {
            lock (_gate)
                return new CacheStats(_entries.Count, Interlocked.Read(ref _hits), Interlocked.Read(ref _shared), Interlocked.Read(ref _loads));
        }
    }

    /// <summary>The value when it is there and fresh; never starts a load (to estimate a loading's requests).</summary>
    public bool TryPeek<T>(string key, out T value) where T : class
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Source.Task.IsCompletedSuccessfully && entry.ExpiresAt > time.GetUtcNow())
            {
                value = (T)entry.Source.Task.Result;
                return true;
            }
        }
        value = null!;
        return false;
    }

    /// <summary>
    /// The value from memory, from a load already running for the same key, or from <paramref name="load"/>, which is then
    /// kept for <paramref name="keepFor"/> (computed after the load; zero = only shared while it runs). A waiter whose
    /// load failed for a reason of the loader's own (its loading was cancelled or its budget ran out) loads once itself;
    /// any other failure (an HTTP error, a block) is the answer for everybody who waited.
    /// </summary>
    public async Task<T> GetAsync<T>(string key, Func<T, TimeSpan> keepFor, Func<Task<T>> load, CancellationToken cancellationToken) where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            Entry? mine = null;
            Entry entry;
            lock (_gate)
            {
                var now = time.GetUtcNow();
                if (_entries.TryGetValue(key, out var existing)
                    && (!existing.Source.Task.IsCompleted || (existing.Source.Task.IsCompletedSuccessfully && existing.ExpiresAt > now)))
                {
                    entry = existing;
                }
                else
                {
                    if (_entries.Count >= SweepAbove)
                        Sweep(now);
                    entry = mine = new Entry(new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously));
                    _entries[key] = entry;
                }
            }

            if (mine is null)
            {
                if (entry.Source.Task.IsCompletedSuccessfully)
                {
                    Interlocked.Increment(ref _hits);
                    return (T)entry.Source.Task.Result;
                }
                try
                {
                    var value = (T)await entry.Source.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _shared);
                    return value;
                }
                catch (Exception ex) when (attempt == 0 && !cancellationToken.IsCancellationRequested
                                           && ex is OperationCanceledException or BudgetExhaustedException)
                {
                    continue; // the loader's own problem: load it ourselves
                }
            }

            Interlocked.Increment(ref _loads);
            try
            {
                var value = await load().ConfigureAwait(false);
                var keep = keepFor(value);
                lock (_gate)
                {
                    mine.ExpiresAt = time.GetUtcNow() + keep;
                    if (keep <= TimeSpan.Zero && _entries.TryGetValue(key, out var current) && ReferenceEquals(current, mine))
                        _entries.Remove(key);
                }
                mine.Source.SetResult(value);
                return value;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, mine))
                        _entries.Remove(key);
                }
                mine.Source.SetException(ex);
                _ = mine.Source.Task.Exception; // observed: nobody may be waiting
                throw;
            }
        }
    }

    /// <summary>Forgets everything (the tests start clean).</summary>
    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries.ToList())
        {
            if (entry.Source.Task.IsCompleted && entry.ExpiresAt <= now)
                _entries.Remove(key);
        }
    }

    // ------------------------------------------------------------------ keys and keeping times

    public static string RepertoireKey(string cinemaId, DateOnly date) => $"repertoire:{cinemaId}:{date:yyyy-MM-dd}";

    public static string DetailsKey(string screeningId) => $"details:{screeningId}";

    public static string SeatPlanKey(int venueId, int seatplanId) => $"seatplan:{venueId}:{seatplanId}";

    public static string FreeSeatsKey(string screeningId) => $"free-seats:{screeningId}";

    /// <summary>
    /// How long a repertoire fetched at <paramref name="now"/> is kept: <see cref="WizardOptions.RepertoireNearMinutes"/>
    /// for today and tomorrow (Polish time), <see cref="WizardOptions.RepertoireLaterMinutes"/> for later days - but a
    /// later day's entry never outlives the moment it becomes "tomorrow" by more than the near time.
    /// </summary>
    public static TimeSpan RepertoireKeep(DateOnly date, DateTimeOffset now, WizardOptions options)
    {
        var near = TimeSpan.FromMinutes(options.RepertoireNearMinutes);
        var later = TimeSpan.FromMinutes(options.RepertoireLaterMinutes);
        var becomesNear = PollerTime.ToUtc(date.AddDays(-1).ToDateTime(TimeOnly.MinValue));
        if (now >= becomesNear)
            return near;
        var keep = becomesNear + near - now;
        return keep < later ? keep : later;
    }

    /// <summary>Polish today (repertoire days start at Polish midnight).</summary>
    public static DateOnly PolishToday(DateTimeOffset now) => DateOnly.FromDateTime(PolishTime.FromUtc(now.UtcDateTime));
}
