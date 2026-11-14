namespace CinemaWatch.Server.Polling;

/// <summary>Where a request may take its token from (a budget, or a loading's reservation).</summary>
public interface IRequestTokens
{
    /// <summary>One token, or false when none is left (the request is not sent).</summary>
    bool TryTake();
}

/// <summary>
/// A request budget (a token bucket): a number of tokens an hour, refilled steadily, at most <see cref="Capacity"/> saved
/// up. Every request to Cinema City takes one token. The poller's global budget is
/// <see cref="PollerOptions.RequestsPerHour"/> with a sixth of an hour's worth saved up at most; the wizard's budget and
/// each account's limit are buckets of the same kind (<see cref="WizardBudget"/>, <see cref="AccountBudgets"/>). Kept in
/// memory: a restart starts with a full bucket. A budget changed in the operator's panel applies at once; a smaller
/// bucket drops the tokens above its new size.
/// </summary>
public sealed class RequestBudget : IRequestTokens
{
    private readonly Func<int> _perHour;
    private readonly Func<int> _capacity;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private double _tokens = double.NaN;
    private DateTimeOffset _updatedAt;

    /// <summary>The poller's global budget.</summary>
    public RequestBudget(PollerSettings settings, TimeProvider time)
        : this(() => settings.Poller.RequestsPerHour, () => Math.Max(10, settings.Poller.RequestsPerHour / 6), time)
    {
    }

    /// <param name="perHour">Tokens an hour (read at every use, so a change applies at once).</param>
    /// <param name="capacity">Most tokens saved up (read at every use).</param>
    public RequestBudget(Func<int> perHour, Func<int> capacity, TimeProvider time)
    {
        _perHour = perHour;
        _capacity = capacity;
        _time = time;
    }

    public int Capacity => _capacity();

    public int PerHour => _perHour();

    /// <summary>Whole tokens available now.</summary>
    public int Available
    {
        get
        {
            lock (_gate)
            {
                Refill();
                return (int)Math.Floor(_tokens);
            }
        }
    }

    public bool TryTake() => TryTake(1);

    public bool TryTake(int count)
    {
        lock (_gate)
        {
            Refill();
            if (_tokens < count)
                return false;
            _tokens -= count;
            return true;
        }
    }

    /// <summary>Gives back tokens taken but not used (a reservation larger than needed); never above the capacity.</summary>
    public void Return(int count)
    {
        if (count <= 0)
            return;
        lock (_gate)
        {
            Refill();
            _tokens = Math.Min(Capacity, _tokens + count);
        }
    }

    /// <summary>
    /// How long until <paramref name="count"/> tokens are available (zero when they are now); null when the bucket
    /// can never hold that many.
    /// </summary>
    public TimeSpan? WaitFor(int count)
    {
        lock (_gate)
        {
            Refill();
            if (count > Capacity)
                return null;
            var missing = count - _tokens;
            if (missing <= 0)
                return TimeSpan.Zero;
            var perHour = _perHour();
            return perHour <= 0 ? null : TimeSpan.FromHours(missing / perHour);
        }
    }

    /// <summary>True when the bucket is full: nothing taken for a while (an account's bucket can then be forgotten).</summary>
    public bool IsFull
    {
        get
        {
            lock (_gate)
            {
                Refill();
                return _tokens >= Capacity;
            }
        }
    }

    private void Refill()
    {
        var now = _time.GetUtcNow();
        var capacity = Capacity;
        if (double.IsNaN(_tokens))
        {
            _tokens = capacity;
            _updatedAt = now;
            return;
        }
        var elapsed = (now - _updatedAt).TotalHours;
        if (elapsed > 0)
            _tokens += elapsed * _perHour();
        _tokens = Math.Min(capacity, _tokens);
        _updatedAt = now;
    }
}

/// <summary>The wizard's own budget (<see cref="WizardOptions.RequestsPerHour"/>, <see cref="WizardOptions.Burst"/> saved up).</summary>
public sealed class WizardBudget(PollerSettings settings, TimeProvider time)
{
    public RequestBudget Bucket { get; } = new(() => settings.Poller.Wizard.RequestsPerHour, () => settings.Poller.Wizard.Burst, time);
}

/// <summary>
/// Each account's limit for the wizard (<see cref="WizardOptions.AccountRequestsPerHour"/> an hour, as many saved up):
/// only requests that miss the cache count. Kept in memory per account id; a full bucket is forgotten, so the
/// dictionary only holds accounts that used the wizard recently. Nothing is stored in the database.
/// </summary>
public sealed class AccountBudgets(PollerSettings settings, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RequestBudget> _buckets = new(StringComparer.Ordinal);

    public RequestBudget For(string userId)
    {
        lock (_gate)
        {
            if (_buckets.Count > 100)
            {
                foreach (var (id, bucket) in _buckets.ToList())
                {
                    if (id != userId && bucket.IsFull)
                        _buckets.Remove(id);
                }
            }
            if (!_buckets.TryGetValue(userId, out var budget))
            {
                budget = new RequestBudget(() => settings.Poller.Wizard.AccountRequestsPerHour, () => settings.Poller.Wizard.AccountRequestsPerHour, time);
                _buckets[userId] = budget;
            }
            return budget;
        }
    }

    /// <summary>Accounts whose limit is partly used now (for the operator's panel; no ids).</summary>
    public int ActiveAccounts
    {
        get
        {
            lock (_gate)
                return _buckets.Values.Count(b => !b.IsFull);
        }
    }
}
