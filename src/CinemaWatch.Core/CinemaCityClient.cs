using System.Globalization;
using System.Net;
using System.Text;

namespace CinemaWatch.Core;

/// <summary>
/// Read-only client for the public Cinema City endpoints used by the website. It never creates a cart or
/// locks seats (that endpoint is protected by Cloudflare Turnstile and is not needed).
/// </summary>
public sealed class CinemaCityClient : IDisposable
{
    private const string WebBase = "https://www.cinema-city.pl";
    private const string TicketsApi = "https://tickets.cinema-city.pl/api";

    private readonly HttpClient _http;

    public CinemaCityClient()
        : this(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
    {
    }

    /// <summary>Creates a client on top of a custom handler (used by tests).</summary>
    public CinemaCityClient(HttpMessageHandler handler, bool disposeHandler = true)
    {
        _http = new HttpClient(handler, disposeHandler) { Timeout = TimeSpan.FromSeconds(30) };
        // The ticketing API expects a client-generated id, like the browser sends.
        _http.DefaultRequestHeaders.Add("uuid", Guid.NewGuid().ToString());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) CinemaWatch/{AppVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl-PL");
    }

    /// <summary>"1.0.0" - the version of this library (set at build time).</summary>
    public static string AppVersion { get; } =
        typeof(CinemaCityClient).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public async Task<DayRepertoire> GetRepertoireAsync(string cinemaId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"{WebBase}/pl/data-api-service/v1/quickbook/{Locations.TenantPoland}/film-events/in-cinema/{Uri.EscapeDataString(cinemaId)}/at-date/{day}?attr=&lang=pl_PL";
        var json = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        return CinemaCityParser.ParseRepertoire(json, date);
    }

    public async Task<Presentation> GetPresentationAsync(string presentationId, CancellationToken cancellationToken = default)
    {
        var url = $"{TicketsApi}/presentations/{Uri.EscapeDataString(presentationId)}?referralMiniSiteId=0";
        var json = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        return CinemaCityParser.ParsePresentation(json);
    }

    public async Task<SeatPlan> GetSeatPlanAsync(int venueId, int seatplanId, CancellationToken cancellationToken = default) =>
        CinemaCityParser.ParseSeatPlan(await GetSeatPlanJsonAsync(venueId, seatplanId, cancellationToken).ConfigureAwait(false));

    /// <summary>Raw seat plan response (cached on disk by <see cref="SeatAvailabilityService"/>).</summary>
    public Task<string> GetSeatPlanJsonAsync(int venueId, int seatplanId, CancellationToken cancellationToken = default)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"{TicketsApi}/seats/seatplanV2?venueId={venueId}&seatplanId={seatplanId}");
        return SendAsync(HttpMethod.Post, url, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetFreeSeatsAsync(string presentationId, int venueTypeId, int isReserved, CancellationToken cancellationToken = default)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{TicketsApi}/seats/seats-statusV2?presentationId={Uri.EscapeDataString(presentationId)}&venueTypeId={venueTypeId}&isReserved={isReserved}");
        var json = await SendAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        return CinemaCityParser.ParseFreeSeats(json);
    }

    private async Task<string> SendAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (method == HttpMethod.Post)
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new CinemaCityException(response.StatusCode, request.RequestUri?.AbsolutePath ?? url);
        return body;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Non-success HTTP response from Cinema City.</summary>
public sealed class CinemaCityException(HttpStatusCode statusCode, string path)
    : Exception($"Cinema City returned HTTP {(int)statusCode} for {path}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Path { get; } = path;
}
