using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CinemaWatch.Server.Polling;

/// <summary>
/// Development fake of Cinema City (docs/SAAS.md §9: the real API only on demand). The poller's and the wizard's whole
/// pipeline runs - the client, the parser, block detection, the request log - only the network is replaced. Every
/// screening id gets the captured Sadyba IMAX screening (hall 13/1). The repertoire of any cinema and day is the
/// captured Sadyba day (16 Dec 2026: 2 films, 25 screenings, 4 of them IMAX without dubbing) moved to that cinema and
/// day, with screening ids made from the cinema, the day and the position (<see cref="FakeScreeningId"/>), so the same
/// request always gives the same ids. Files in the folder steer the answers:
/// <list type="bullet">
/// <item><c>free-&lt;screening id&gt;.txt</c>: seat keys (e.g. <c>1_18_4</c>) separated by spaces, commas or lines are free;
/// without the file the screening is sold out.</item>
/// <item><c>block.txt</c>: every answer is a block - HTTP 403 with <c>cf-mitigated: challenge</c>, or HTTP 429 when the file
/// starts with 429 (a second number = Retry-After in seconds).</item>
/// <item><c>error500.txt</c>: every answer is HTTP 500; <c>error500-&lt;screening id&gt;.txt</c> only for that screening.</item>
/// </list>
/// </summary>
public sealed class FakeCinemaCityHandler(string directory) : HttpMessageHandler
{
    public const string ReadmeFile = "README.txt";

    private static readonly Lazy<string> PresentationTemplate = new(() => Resource("presentation.json"));
    private static readonly Lazy<string> SeatPlan = new(() => Resource("seatplan.json"));
    private static readonly Lazy<string> RepertoireTemplate = new(() => Resource("repertoire.json"));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(request.RequestUri!));

    private HttpResponseMessage Answer(Uri uri)
    {
        var path = uri.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var screeningId = path.Contains("/presentations/", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..])
            : query["presentationId"];

        if (ReadFile("block.txt") is { } block)
        {
            var parts = block.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0] == "429")
            {
                var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("Too many requests (fake)") };
                if (parts.Length > 1 && int.TryParse(parts[1], out var seconds) && seconds > 0)
                    tooMany.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                return tooMany;
            }
            var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<!DOCTYPE html><html><title>Just a moment...</title><body>Fake Cloudflare challenge</body></html>", Encoding.UTF8, "text/html"),
            };
            challenge.Headers.Add("cf-mitigated", "challenge");
            return challenge;
        }
        if (ReadFile("error500.txt") is not null || (screeningId is not null && ReadFile($"error500-{screeningId}.txt") is not null))
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("Internal server error (fake)") };

        if (path.Contains("/film-events/in-cinema/", StringComparison.Ordinal) && Repertoire(path) is { } repertoire)
            return Json(repertoire);
        if (path.Contains("/presentations/", StringComparison.Ordinal) && screeningId is not null)
            return Json(Presentation(screeningId));
        if (path.EndsWith("/seatplanV2", StringComparison.Ordinal))
            return Json(SeatPlan.Value);
        if (path.EndsWith("/seats-statusV2", StringComparison.Ordinal) && screeningId is not null)
            return Json(FreeSeats(screeningId));
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not found (fake)") };
    }

    /// <summary>Writes a short explanation into the folder (once), so the folder explains itself.</summary>
    public static void PrepareFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        var readme = Path.Combine(directory, ReadmeFile);
        if (File.Exists(readme))
            return;
        File.WriteAllText(readme, """
            CinemaWatch development fake of Cinema City (used when Saas:Poller:Source = Fake).
            Every screening gets the captured Sadyba IMAX hall (13/1). Every cinema's repertoire on any day is the
            captured Sadyba day of 16 Dec 2026 moved to that cinema and day (4 IMAX screenings without dubbing); the
            screening ids are <cinema><yyMMdd><position>, e.g. 106026121613. Files in this folder steer the answers:

              free-<screening id>.txt   seat keys that are free, e.g. "1_18_4 1_19_4" (spaces, commas or lines);
                                        without the file the screening is sold out
              block.txt                 every answer is a block: HTTP 403 with cf-mitigated: challenge;
                                        "429 120" inside = HTTP 429 with Retry-After 120 seconds
              error500.txt              every answer is HTTP 500
              error500-<screening id>.txt   HTTP 500 only for that screening

            Changes apply to the next request; nothing needs a restart. Delete the folder to start again.

            """.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    private string? ReadFile(string name)
    {
        // Screening ids are letters and digits (checked at import), so the name cannot leave the folder.
        if (name.IndexOfAny(['/', '\\']) >= 0 || name.Contains("..", StringComparison.Ordinal))
            return null;
        var path = Path.Combine(directory, name);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>"106026121607": cinema 1060, 16 Dec 2026, the 8th screening of the captured day (from 0).</summary>
    public static string FakeScreeningId(string cinemaId, DateOnly date, int index) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{cinemaId}{date:yyMMdd}{index:00}");

    /// <summary>.../film-events/in-cinema/{cinema}/at-date/{yyyy-MM-dd}: the captured day moved there; null for another path.</summary>
    private static string? Repertoire(string path)
    {
        var parts = path.Split('/');
        var at = Array.IndexOf(parts, "in-cinema");
        if (at < 0 || at + 3 >= parts.Length || parts[at + 2] != "at-date"
            || !DateOnly.TryParseExact(parts[at + 3], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
        {
            return null;
        }
        var cinemaId = Uri.UnescapeDataString(parts[at + 1]);
        var day = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var json = JsonNode.Parse(RepertoireTemplate.Value)!;
        var events = json["body"]!["events"]!.AsArray();
        for (var i = 0; i < events.Count; i++)
        {
            var original = events[i]!;
            var oldId = original["id"]!.GetValue<string>();
            var moved = JsonNode.Parse(original.ToJsonString().Replace(oldId, FakeScreeningId(cinemaId, date, i), StringComparison.Ordinal))!;
            moved["cinemaId"] = cinemaId;
            moved["businessDay"] = day;
            moved["eventDateTime"] = day + moved["eventDateTime"]!.GetValue<string>()[10..];
            events[i] = moved;
        }
        return json.ToJsonString();
    }

    private static string Presentation(string screeningId)
    {
        var json = JsonNode.Parse(PresentationTemplate.Value)!;
        json["presentation"]!["id"] = long.TryParse(screeningId, out var numeric) ? JsonValue.Create(numeric) : JsonValue.Create(screeningId);
        return json.ToJsonString();
    }

    private string FreeSeats(string screeningId)
    {
        var keys = (ReadFile($"free-{screeningId}.txt") ?? "")
            .Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal);
        var seats = new JsonObject();
        foreach (var key in keys)
            seats[key] = 0;
        return new JsonObject { ["seats"] = seats }.ToJsonString();
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Resource(string name)
    {
        using var stream = typeof(FakeCinemaCityHandler).Assembly.GetManifestResourceStream("CinemaWatch.Server.Fake." + name)
            ?? throw new InvalidOperationException($"Embedded fake response {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().TrimStart('\uFEFF');
    }
}
