using System.Collections.Concurrent;
using CinemaWatch.Server.Data;

namespace CinemaWatch.Server.Polling;

/// <summary>
/// Adds service events (table service_events) to a database context; the caller saves. Events that could repeat every
/// cycle (an exhausted budget, a failing mail server, a failing cycle) are throttled, so the table and the operator's
/// view do not fill up with copies.
/// </summary>
public sealed class ServiceEvents(TimeProvider time)
{
    /// <summary>The same throttled kind is recorded at most this often.</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<ServiceEventKind, DateTimeOffset> _last = new();

    public void Add(AppDbContext db, ServiceEventKind kind, string message, string? data = null, bool throttled = false)
    {
        var now = time.GetUtcNow();
        if (throttled)
        {
            if (_last.TryGetValue(kind, out var last) && now - last < Throttle)
                return;
            _last[kind] = now;
        }
        db.ServiceEvents.Add(new ServiceEvent
        {
            At = now,
            Kind = kind,
            Message = message.Length <= 1000 ? message : message[..1000],
            Data = data,
        });
    }
}
