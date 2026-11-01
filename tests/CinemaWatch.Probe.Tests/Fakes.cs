using System.Net;
using System.Text;
using CinemaWatch.Core;

namespace CinemaWatch.Tests;

/// <summary>Cinema City stand-in: captured responses by default, any response or exception via <see cref="Override"/>.</summary>
internal sealed class FakeNetwork : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string UserAgent)> Requests { get; } = [];

    /// <summary>Return a response to replace the captured one, or null to keep it.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public int Count(string pathPart) => Requests.Count(r => r.Uri.AbsolutePath.Contains(pathPart, StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add((request.Method, uri, request.Headers.UserAgent.ToString()));
        if (Override?.Invoke(request) is { } custom)
            return Task.FromResult(custom);

        var path = uri.AbsolutePath;
        var fixture = path switch
        {
            _ when path.Contains("/film-events/in-cinema/1060/at-date/2026-12-16", StringComparison.Ordinal) => "repertoire-1060-2026-12-16.json",
            _ when path.Contains("/presentations/", StringComparison.Ordinal) => "presentation-1765592.json",
            _ when path.EndsWith("/seatplanV2", StringComparison.Ordinal) => "seatplan-13-1.json",
            _ when path.EndsWith("/seats-statusV2", StringComparison.Ordinal) => "seats-status-1765592.json",
            _ => null,
        };
        return Task.FromResult(fixture is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Respond(200, Fixtures.Read(fixture), "application/json"));
    }

    public static HttpResponseMessage Respond(int status, string body, string mediaType, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>Row 4 of Sadyba IMAX (31 seats, 29 of them free in the captured seat status).</summary>
    public static string[] Row4Keys() =>
        CinemaCityParser.ParseSeatPlan(Read("seatplan-13-1.json")).SeatsInRow("4").Select(s => s.Key).ToArray();

    public static WatchProfile Profile(string screeningId, params string[] seatKeys) => new()
    {
        Name = "Diuna Warszawa",
        CinemaId = "1060",
        CinemaName = "Cinema City Sadyba",
        CinemaIds = ["1060"],
        FilmId = "8105s2r",
        FilmName = "Diuna: Część trzecia",
        DateFrom = new DateOnly(2026, 12, 16),
        DateTo = new DateOnly(2026, 12, 16),
        Screenings =
        [
            new WatchedScreening
            {
                Id = screeningId,
                CinemaId = "1060",
                City = "Warszawa",
                StartsAt = new DateTime(2026, 12, 16, 16, 0, 0),
                Auditorium = "IMAX BNP Paribas",
                BookingLink = $"https://tickets.cinema-city.pl/api/order/{screeningId}?lang=pl",
                SeatKeys = seatKeys.ToList(),
            },
        ],
    };
}

/// <summary>Profiles and data folders of one test, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "cinemawatch-probe-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Profiles);
    }

    public string Root { get; }
    public string Profiles => Path.Combine(Root, "profiles");
    public string Data => Path.Combine(Root, "data");

    public void WriteProfile(string fileName, WatchProfile profile) =>
        ProfileStore.Save(Path.Combine(Profiles, fileName + ProfileStore.FileExtension), profile);

    public string[] CsvRows() =>
        Directory.EnumerateFiles(Data, "requests-*.csv").SelectMany(File.ReadAllLines).Where(l => !l.StartsWith("time_utc", StringComparison.Ordinal)).ToArray();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
