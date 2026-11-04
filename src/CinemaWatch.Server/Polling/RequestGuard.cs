using System.Diagnostics;
using System.Text;

namespace CinemaWatch.Server.Polling;

/// <summary>One request to Cinema City as the poller saw it (one row of check_log).</summary>
public sealed record RequestRecord(
    DateTimeOffset At,
    string? ScreeningId,
    bool Manual,
    string Method,
    string Host,
    string Endpoint,
    string Target,
    int? Status,
    long DurationMs,
    string? ContentType,
    string? CfMitigated,
    string? CfRay,
    string? RetryAfter,
    string? Error)
{
    /// <summary>"HTTP 403 (cf-mitigated: challenge) on GET tickets.cinema-city.pl/api/presentations/1765592"</summary>
    public string Describe() =>
        (Status is { } status ? $"HTTP {status}" : Error ?? "no response")
        + (string.IsNullOrEmpty(CfMitigated) ? "" : $" (cf-mitigated: {CfMitigated})")
        + (Status is >= 200 and < 300 && ContentType is not null ? $" ({ContentType})" : "")
        + $" on {Method} {Host}{Target}";

    /// <summary>Short name of the endpoint, so the log can be grouped without parsing URLs.</summary>
    public static string EndpointOf(Uri uri) => uri.AbsolutePath switch
    {
        var p when p.Contains("/film-events/", StringComparison.Ordinal) => "repertoire",
        var p when p.Contains("/presentations/", StringComparison.Ordinal) => "presentation",
        var p when p.EndsWith("/seatplanV2", StringComparison.Ordinal) => "seatplan",
        var p when p.EndsWith("/seats-statusV2", StringComparison.Ordinal) => "seats-status",
        var p => p.Length <= 100 ? p : p[..100],
    };
}

/// <summary>Thrown instead of sending a request once an earlier request of the cycle was blocked.</summary>
public sealed class PollerBlockedException(RequestRecord blockedBy)
    : Exception($"Not sent: an earlier request of this cycle was blocked ({blockedBy.Describe()}).");

/// <summary>Thrown instead of sending a request when the global request budget is used up.</summary>
public sealed class BudgetExhaustedException() : Exception("Not sent: the request budget is used up for now.");

/// <summary>
/// Sits between the Cinema City client and the network for one poller cycle. Before the first request of every
/// screening after the first it waits (<see cref="IRequestPacer"/>; the requests of one screening follow each other
/// directly), and before every request it takes a token from the <see cref="RequestBudget"/>; it records every
/// request; and it recognises a block - HTTP 403 or 429, a response Cloudflare marked as mitigated (a challenge), or an
/// HTML page where the API answers JSON - after which nothing more is sent in the cycle (KB §3.6: no retry storms).
/// </summary>
public sealed class RequestGuard : HttpMessageHandler
{
    /// <summary>Longest part of a blocked response kept for diagnosis.</summary>
    public const int MaxBlockedBodyChars = 16 * 1024;

    private readonly HttpMessageInvoker _network;
    private readonly IRequestPacer _pacer;
    private readonly RequestBudget _budget;
    private readonly TimeProvider _time;
    private readonly List<RequestRecord> _records = [];
    private string? _lastScreeningId;

    public RequestGuard(HttpMessageHandler network, IRequestPacer pacer, RequestBudget budget, TimeProvider time)
    {
        _network = new HttpMessageInvoker(network, disposeHandler: true);
        _pacer = pacer;
        _budget = budget;
        _time = time;
    }

    /// <summary>The screening the next requests belong to (for the log).</summary>
    public string? CurrentScreeningId { get; set; }

    /// <summary>Whether the current screening is checked because of "Check now" (for the log).</summary>
    public bool CurrentManual { get; set; }

    public IReadOnlyList<RequestRecord> Records => _records;

    /// <summary>The first blocked request of the cycle, or null.</summary>
    public RequestRecord? BlockedBy { get; private set; }

    /// <summary>Start of the blocked response (e.g. a Cloudflare challenge page).</summary>
    public string? BlockedBody { get; private set; }

    /// <summary>How long the blocked response asked to wait (Retry-After), if it said so.</summary>
    public TimeSpan? RetryAfter { get; private set; }

    /// <summary>The block is the first response with any of these signs.</summary>
    public static bool IsBlock(int status, string? cfMitigated, string? contentType) =>
        status is 403 or 429
        || !string.IsNullOrEmpty(cfMitigated)
        || (status is >= 200 and < 300 && string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (BlockedBy is not null)
            throw new PollerBlockedException(BlockedBy);
        if (_records.Count > 0 && CurrentScreeningId != _lastScreeningId)
            await _pacer.PauseAsync(CurrentManual, cancellationToken).ConfigureAwait(false);
        _lastScreeningId = CurrentScreeningId;
        if (!_budget.TryTake())
            throw new BudgetExhaustedException();

        var uri = request.RequestUri!;
        var at = _time.GetUtcNow();
        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _network.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // Read the whole body now, so the duration covers the download.
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException ? "timeout" : $"{ex.GetType().Name}: {ex.Message}";
            _records.Add(new RequestRecord(at, CurrentScreeningId, CurrentManual, request.Method.Method, uri.Host, RequestRecord.EndpointOf(uri),
                Shorten(uri.PathAndQuery, 500), null, watch.ElapsedMilliseconds, null, null, null, null, Shorten(error, 500)));
            throw;
        }

        var status = (int)response.StatusCode;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var cfMitigated = Header(response, "cf-mitigated");
        var record = new RequestRecord(at, CurrentScreeningId, CurrentManual, request.Method.Method, uri.Host, RequestRecord.EndpointOf(uri),
            Shorten(uri.PathAndQuery, 500), status, watch.ElapsedMilliseconds, contentType, cfMitigated, Header(response, "cf-ray"),
            response.Headers.RetryAfter?.ToString(), null);
        _records.Add(record);

        if (IsBlock(status, cfMitigated, contentType))
        {
            BlockedBy = record;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            BlockedBody = body.Length <= MaxBlockedBodyChars ? body : body[..MaxBlockedBodyChars];
            RetryAfter = response.Headers.RetryAfter switch
            {
                { Delta: { } delta } => delta,
                { Date: { } date } when date > at => date - at,
                _ => null,
            };
        }
        return response;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? Shorten(string.Join(", ", values), 100) : null;

    private static string Shorten(string value, int max) => value.Length <= max ? value : value[..max];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _network.Dispose();
        base.Dispose(disposing);
    }
}
