using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Operations;

/// <summary>
/// The heartbeat (configuration section "Saas:Heartbeat"): the address of an external "dead man's switch" such as a
/// Healthchecks.io check. The address works like a password - whoever knows it can report "alive" in the server's
/// name - so it comes from user secrets or an environment variable, never from a file in the repository. Empty = off.
/// </summary>
public sealed class HeartbeatOptions
{
    public const string Section = "Saas:Heartbeat";

    public string Url { get; set; } = "";

    /// <summary>At most one ping in this time; the external check's period should be at least this long.</summary>
    public int IntervalMinutes { get; set; } = 5;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Url);

    public Uri? Target => IsEnabled && Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) ? uri : null;

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    /// <summary>Empty, or an https address without a user name or password in it.</summary>
    public bool IsValid() =>
        IntervalMinutes is >= 1 and <= 60
        && (!IsEnabled || Target is { Scheme: "https" } target && target.UserInfo.Length == 0);
}

/// <summary>Sends one ping and returns the HTTP status. Tests replace it.</summary>
public interface IHeartbeatClient
{
    Task<int> PingAsync(Uri target, CancellationToken cancellationToken);
}

/// <summary>A plain GET with a short timeout and the server's honest User-Agent.</summary>
public sealed class HttpHeartbeatClient : IHeartbeatClient, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client = new() { Timeout = Timeout };

    public HttpHeartbeatClient() => _client.DefaultRequestHeaders.UserAgent.ParseAdd(PollerInfo.UserAgent);

    public async Task<int> PingAsync(Uri target, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// After every poller cycle that finished (idle, paused and blocked cycles included: the process lives), at most every
/// <see cref="HeartbeatOptions.IntervalMinutes"/>, tells the external service that the server is alive. A cycle that
/// throws sends nothing, so a poller that keeps failing or hangs raises the alarm as well as a dead server or process.
/// The ping is not a Cinema City request: no budget, no request log. A failure is recorded (component_status, a
/// throttled event) and never stops the poller. The address is never written to a log.
/// </summary>
public sealed class Heartbeat(
    IOptions<HeartbeatOptions> options,
    IHeartbeatClient client,
    IServiceScopeFactory scopes,
    ServiceEvents events,
    TimeProvider time,
    ILogger<Heartbeat> logger)
{
    private DateTimeOffset? _lastAttempt;

    /// <returns>True when a ping was sent (successfully or not).</returns>
    public async Task<bool> AfterCycleAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (settings.Target is not { } target)
            return false;
        var now = time.GetUtcNow();
        if (_lastAttempt is { } last && now - last < settings.Interval)
            return false;
        _lastAttempt = now;

        string? failure = null;
        try
        {
            var status = await client.PingAsync(target, cancellationToken).ConfigureAwait(false);
            if (status is < 200 or >= 300)
                failure = $"HTTP {status}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException or TimeoutException ? "timeout" : $"{ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ComponentStatuses.RecordAsync(db, ComponentStatusRow.Heartbeat, now, failure, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                events.Add(db, ServiceEventKind.HeartbeatFailed, $"Heartbeat not delivered: {failure}", throttled: true);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The heartbeat's outcome could not be recorded");
        }
        if (failure is not null)
            logger.LogWarning("Heartbeat not delivered: {Failure}", failure);
        return true;
    }
}
