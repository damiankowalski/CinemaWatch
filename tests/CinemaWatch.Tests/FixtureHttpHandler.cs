using System.Net;
using System.Text;

namespace CinemaWatch.Tests;

/// <summary>Serves saved API responses from the Fixtures folder and records the requests made.</summary>
internal sealed class FixtureHttpHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string? Uuid)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        request.Headers.TryGetValues("uuid", out var uuids);
        Requests.Add((request.Method, uri, uuids?.FirstOrDefault()));

        var path = uri.AbsolutePath;
        string? fixture = path switch
        {
            _ when path.Contains("/film-events/in-cinema/1060/at-date/2026-12-16") => "repertoire-1060-2026-12-16.json",
            _ when path.EndsWith("/presentations/1765592") => "presentation-1765592.json",
            _ when path.EndsWith("/seats/seatplanV2") && request.Method == HttpMethod.Post => "seatplan-13-1.json",
            _ when path.EndsWith("/seats/seats-statusV2") && uri.Query.Contains("presentationId=1765592") => "seats-status-1765592.json",
            _ => null,
        };

        if (fixture is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}
