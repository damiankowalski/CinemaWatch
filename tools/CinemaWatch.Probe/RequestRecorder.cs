using System.Diagnostics;
using System.Globalization;

namespace CinemaWatch.Probe;

/// <summary>One request sent to Cinema City, as measured by the probe (one row of the CSV log).</summary>
public sealed record RequestRecord(
    DateTimeOffset At,
    string Run,
    string Method,
    string Host,
    string Endpoint,
    string Target,
    int? Status,
    long DurationMs,
    long? Bytes,
    string? ContentType,
    string? CfMitigated,
    string? CfRay,
    string? RetryAfter,
    string? Error)
{
    /// <summary>
    /// A sign of blocking: HTTP 403 or 429, or any response Cloudflare marked as mitigated (a challenge page).
    /// After one of these the probe sends nothing more in the run (KB §3.6: no retry storms).
    /// </summary>
    public bool IsBlock => Status is 403 or 429 || !string.IsNullOrEmpty(CfMitigated);

    /// <summary>"HTTP 403 (cf-mitigated: challenge) on GET tickets.cinema-city.pl/api/presentations/1765592"</summary>
    public string Describe() =>
        $"{(Status is { } s ? $"HTTP {s}" : Error)}"
        + (string.IsNullOrEmpty(CfMitigated) ? "" : $" (cf-mitigated: {CfMitigated})")
        + $" on {Method} {Host}{Target}";

    /// <summary>Short name of the API endpoint, so the log can be grouped without parsing URLs.</summary>
    public static string EndpointOf(Uri uri) => uri.AbsolutePath switch
    {
        var p when p.Contains("/film-events/", StringComparison.Ordinal) => "repertoire",
        var p when p.Contains("/presentations/", StringComparison.Ordinal) => "presentation",
        var p when p.EndsWith("/seatplanV2", StringComparison.Ordinal) => "seatplan",
        var p when p.EndsWith("/seats-statusV2", StringComparison.Ordinal) => "seats-status",
        var p => p,
    };
}

/// <summary>Thrown instead of sending a request after an earlier request of the same run was blocked.</summary>
public sealed class ProbeStoppedException(RequestRecord blockedBy)
    : Exception($"Not sent: an earlier request in this run was blocked ({blockedBy.Describe()}).")
{
    public RequestRecord BlockedBy { get; } = blockedBy;
}

/// <summary>
/// Sits between the Cinema City client and the network. Writes one CSV row per request (time, endpoint, status,
/// duration, size, Cloudflare headers) and stops the run at the first sign of blocking: later requests are not sent.
/// </summary>
public sealed class RequestRecorder : HttpMessageHandler
{
    /// <summary>Largest response body kept for diagnosis when a request is blocked.</summary>
    public const int MaxBlockedBodyBytes = 256 * 1024;

    private readonly HttpMessageInvoker _network;
    private readonly RequestLog _log;
    private readonly TimeProvider _clock;
    private readonly string _run;
    private readonly List<RequestRecord> _records = [];

    public RequestRecorder(HttpMessageHandler network, bool disposeNetwork, RequestLog log, TimeProvider clock, string run)
    {
        _network = new HttpMessageInvoker(network, disposeNetwork);
        _log = log;
        _clock = clock;
        _run = run;
    }

    public IReadOnlyList<RequestRecord> Records => _records;

    /// <summary>The first blocked request of the run, or null.</summary>
    public RequestRecord? BlockedBy { get; private set; }

    /// <summary>Body of the blocked response (e.g. a Cloudflare challenge page), at most <see cref="MaxBlockedBodyBytes"/>.</summary>
    public byte[]? BlockedBody { get; private set; }

    /// <summary>Requests not sent because the run was already blocked.</summary>
    public int Skipped { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (BlockedBy is not null)
        {
            Skipped++;
            throw new ProbeStoppedException(BlockedBy);
        }

        var uri = request.RequestUri!;
        var at = _clock.GetUtcNow();
        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _network.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // Read the whole body now, so the duration covers the download and the size is known.
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException ? "timeout" : $"{ex.GetType().Name}: {ex.Message}";
            Add(new RequestRecord(at, _run, request.Method.Method, uri.Host, RequestRecord.EndpointOf(uri), uri.PathAndQuery,
                null, watch.ElapsedMilliseconds, null, null, null, null, null, error));
            throw;
        }

        var record = new RequestRecord(at, _run, request.Method.Method, uri.Host, RequestRecord.EndpointOf(uri), uri.PathAndQuery,
            (int)response.StatusCode, watch.ElapsedMilliseconds, response.Content.Headers.ContentLength,
            response.Content.Headers.ContentType?.MediaType, Header(response, "cf-mitigated"), Header(response, "cf-ray"),
            response.Headers.RetryAfter?.ToString(), null);
        Add(record);
        if (record.IsBlock && BlockedBy is null)
        {
            BlockedBy = record;
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            BlockedBody = body.Length <= MaxBlockedBodyBytes ? body : body[..MaxBlockedBodyBytes];
        }
        return response;
    }

    private void Add(RequestRecord record)
    {
        _records.Add(record);
        _log.Append(record);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _network.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Request log: one CSV file per month (UTC), e.g. requests-2026-11.csv, with a header row.</summary>
public sealed class RequestLog(string directory)
{
    public const string Header =
        "time_utc,run,method,host,endpoint,target,status,duration_ms,bytes,content_type,cf_mitigated,cf_ray,retry_after,error";

    private readonly Lock _gate = new();

    public string Directory { get; } = directory;

    public string PathFor(DateTimeOffset at) =>
        Path.Combine(Directory, string.Create(CultureInfo.InvariantCulture, $"requests-{at.UtcDateTime:yyyy-MM}.csv"));

    /// <summary>Creates the folder and the month's file if needed (fails early when the folder is not writable).</summary>
    public void EnsureFile(DateTimeOffset at)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(at);
        lock (_gate)
        {
            if (!File.Exists(path))
                File.WriteAllText(path, Header + "\n");
        }
    }

    public void Append(RequestRecord r)
    {
        var line = Csv.Line(
            r.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            r.Run, r.Method, r.Host, r.Endpoint, r.Target,
            r.Status?.ToString(CultureInfo.InvariantCulture),
            r.DurationMs.ToString(CultureInfo.InvariantCulture),
            r.Bytes?.ToString(CultureInfo.InvariantCulture),
            r.ContentType, r.CfMitigated, r.CfRay, r.RetryAfter, r.Error);
        EnsureFile(r.At);
        lock (_gate)
            File.AppendAllText(PathFor(r.At), line + "\n");
    }
}

/// <summary>CSV values quoted only when needed (comma, quote or line break), quotes doubled.</summary>
public static class Csv
{
    public static string Line(params string?[] values) => string.Join(",", values.Select(Value));

    public static string Value(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var flat = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return flat.IndexOfAny([',', '"']) >= 0 ? "\"" + flat.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : flat;
    }
}
