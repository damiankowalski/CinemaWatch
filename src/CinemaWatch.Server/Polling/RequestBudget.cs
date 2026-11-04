using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Polling;

/// <summary>
/// Global request budget of the server (a token bucket): <see cref="PollerOptions.RequestsPerHour"/> tokens an hour,
/// refilled steadily, at most a sixth of an hour's worth saved up. Every request to Cinema City takes one token,
/// scheduled and manual alike. Kept in memory: a restart starts with a full bucket, which is at most a few requests.
/// </summary>
public sealed class RequestBudget(IOptions<PollerOptions> options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private double _tokens = double.NaN;
    private DateTimeOffset _updatedAt;

    public int Capacity => Math.Max(10, options.Value.RequestsPerHour / 6);

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

    public bool TryTake(int count = 1)
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

    private void Refill()
    {
        var now = time.GetUtcNow();
        if (double.IsNaN(_tokens))
        {
            _tokens = Capacity;
            _updatedAt = now;
            return;
        }
        var elapsed = (now - _updatedAt).TotalHours;
        if (elapsed > 0)
            _tokens = Math.Min(Capacity, _tokens + elapsed * options.Value.RequestsPerHour);
        _updatedAt = now;
    }
}
