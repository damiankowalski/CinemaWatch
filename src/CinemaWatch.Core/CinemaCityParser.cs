using System.Globalization;
using System.Text.Json;

namespace CinemaWatch.Core;

/// <summary>
/// Parses Cinema City API responses into domain models. Kept separate from the HTTP client so it can be
/// tested against saved responses.
/// </summary>
public static class CinemaCityParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- repertoire (www.cinema-city.pl)

    public static DayRepertoire ParseRepertoire(string json, DateOnly date)
    {
        var response = JsonSerializer.Deserialize<RepertoireResponse>(json, Options)
            ?? throw new JsonException("Empty repertoire response.");

        var films = (response.Body?.Films ?? [])
            .Where(f => !string.IsNullOrEmpty(f.Id))
            .Select(f => new Film(f.Id!, f.Name ?? f.Id!, f.Length, f.Link))
            .ToList();

        var screenings = (response.Body?.Events ?? [])
            .Where(e => !string.IsNullOrEmpty(e.Id) && !string.IsNullOrEmpty(e.FilmId))
            .Select(e => new Screening(
                Id: e.Id!,
                FilmId: e.FilmId!,
                CinemaId: e.CinemaId ?? "",
                StartsAt: e.EventDateTime,
                Auditorium: e.Auditorium ?? "",
                Attributes: e.AttributeIds ?? [],
                OriginalLanguages: e.Languages?.Original ?? [],
                SubtitleLanguages: e.Languages?.Subtitles ?? [],
                DubbedLanguages: e.Languages?.Dubbed ?? [],
                SoldOut: e.SoldOut,
                AvailabilityRatio: e.AvailabilityRatio,
                BookingLink: e.BookingLink ?? ""))
            .ToList();

        return new DayRepertoire(date, films, screenings);
    }

    // ---------------------------------------------------------------- presentation (tickets.cinema-city.pl)

    public static Presentation ParsePresentation(string json)
    {
        var p = JsonSerializer.Deserialize<PresentationResponse>(json, Options)?.Presentation
            ?? throw new JsonException("Response does not contain 'presentation'.");

        return new Presentation(
            Id: p.Id.ToString(CultureInfo.InvariantCulture),
            FilmName: p.FeatureName ?? "",
            StartsAt: ParseLocalDateTime(p.DateTime) ?? throw new JsonException("Presentation has no dateTime."),
            VenueName: p.VenueName ?? "",
            VenueId: p.VenueId,
            SeatplanId: p.SeatplanId,
            VenueTypeId: p.VenueTypeId,
            IsReserved: p.IsReserved,
            SoldOut: p.Soldout != 0,
            TicketSaleStop: ParseLocalDateTime(p.TicketSaleStop));
    }

    // ---------------------------------------------------------------- seat plan (tickets.cinema-city.pl)

    /// <summary>
    /// Seat plan structure: S{section}.G{group}.R{row}.S{seat}. Maps may come either as JSON objects or,
    /// for sequential keys, as arrays - both are handled.
    /// </summary>
    public static SeatPlan ParseSeatPlan(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var seatRadius = 30.0;
        if (root.TryGetProperty("rd", out var planRd) && TryGetDouble(planRd, "seatRadius", out var r) && r > 0)
            seatRadius = r;

        var rows = new List<SeatRow>();
        var seats = new List<Seat>();
        if (!root.TryGetProperty("S", out var sections))
            throw new JsonException("Seat plan has no sections ('S').");

        foreach (var (sectionKey, section) in EnumerateMap(sections))
        {
            if (!section.TryGetProperty("G", out var groups)) continue;
            foreach (var (_, group) in EnumerateMap(groups))
            {
                double offsetX = 0, offsetY = 0;
                if (group.TryGetProperty("rd", out var groupRd))
                {
                    TryGetDouble(groupRd, "x", out offsetX);
                    TryGetDouble(groupRd, "y", out offsetY);
                }
                if (!group.TryGetProperty("R", out var groupRows)) continue;

                foreach (var (rowKey, row) in EnumerateMap(groupRows))
                {
                    var rowLabel = GetString(row, "n") ?? rowKey;
                    PlanPoint? left = null, right = null;
                    if (row.TryGetProperty("rd", out var rowRd) && rowRd.ValueKind == JsonValueKind.Object)
                    {
                        left = TryGetPoint(rowRd, "l", offsetX, offsetY);
                        right = TryGetPoint(rowRd, "r", offsetX, offsetY);
                    }
                    rows.Add(new SeatRow(rowKey, rowLabel, left, right));

                    if (!row.TryGetProperty("S", out var rowSeats)) continue;
                    foreach (var (seatKey, seat) in EnumerateMap(rowSeats))
                    {
                        if (!int.TryParse(seatKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
                            continue;
                        double cx = 0, cy = 0;
                        if (seat.TryGetProperty("rd", out var seatRd))
                        {
                            TryGetDouble(seatRd, "cx", out cx);
                            TryGetDouble(seatRd, "cy", out cy);
                        }
                        var wheelchair = TryGetDouble(seat, "hc", out var hc) && hc != 0;
                        seats.Add(new Seat(
                            Key: $"{sectionKey}_{seatKey}_{rowKey}",
                            Section: sectionKey,
                            RowKey: rowKey,
                            Row: rowLabel,
                            Label: GetString(seat, "n") ?? seatKey,
                            Position: position,
                            X: cx + offsetX,
                            Y: cy + offsetY,
                            IsWheelchair: wheelchair));
                    }
                }
            }
        }

        return new SeatPlan(seatRadius, rows, seats);
    }

    // ---------------------------------------------------------------- seat status (tickets.cinema-city.pl)

    /// <summary>
    /// The status endpoint lists only seats that can still be bought (value 0). Seats missing from the list
    /// are taken. A sold-out screening may return an empty object or an empty array.
    /// </summary>
    public static IReadOnlySet<string> ParseFreeSeats(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var free = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("seats", out var seats) && seats.ValueKind == JsonValueKind.Object)
        {
            foreach (var seat in seats.EnumerateObject())
            {
                if (seat.Value.ValueKind == JsonValueKind.Number && seat.Value.TryGetInt32(out var status) && status == 0)
                    free.Add(seat.Name);
            }
        }
        return free;
    }

    // ---------------------------------------------------------------- helpers

    private static DateTime? ParseLocalDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] formats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm"];
        return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : null;
    }

    private static IEnumerable<(string Key, JsonElement Value)> EnumerateMap(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                yield return (property.Name, property.Value);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                yield return ((index++).ToString(CultureInfo.InvariantCulture), item);
        }
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
            return false;
        switch (property.ValueKind)
        {
            case JsonValueKind.Number:
                return property.TryGetDouble(out value);
            case JsonValueKind.String:
                return double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            case JsonValueKind.True:
                value = 1;
                return true;
            default:
                return false;
        }
    }

    private static PlanPoint? TryGetPoint(JsonElement element, string name, double offsetX, double offsetY)
    {
        if (!element.TryGetProperty(name, out var point)) return null;
        if (!TryGetDouble(point, "x", out var x) || !TryGetDouble(point, "y", out var y)) return null;
        return new PlanPoint(x + offsetX, y + offsetY);
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- DTOs

    private sealed class RepertoireResponse
    {
        public RepertoireBody? Body { get; set; }
    }

    private sealed class RepertoireBody
    {
        public List<FilmDto>? Films { get; set; }
        public List<EventDto>? Events { get; set; }
    }

    private sealed class FilmDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int Length { get; set; }
        public string? Link { get; set; }
    }

    private sealed class EventDto
    {
        public string? Id { get; set; }
        public string? FilmId { get; set; }
        public string? CinemaId { get; set; }
        public DateTime EventDateTime { get; set; }
        public List<string>? AttributeIds { get; set; }
        public LanguagesDto? Languages { get; set; }
        public string? BookingLink { get; set; }
        public bool SoldOut { get; set; }
        public double AvailabilityRatio { get; set; }
        public string? Auditorium { get; set; }
    }

    private sealed class LanguagesDto
    {
        public List<string>? Original { get; set; }
        public List<string>? Dubbed { get; set; }
        public List<string>? Subtitles { get; set; }
    }

    private sealed class PresentationResponse
    {
        public PresentationDto? Presentation { get; set; }
    }

    private sealed class PresentationDto
    {
        public long Id { get; set; }
        public string? FeatureName { get; set; }
        public string? DateTime { get; set; }
        public string? VenueName { get; set; }
        public int VenueId { get; set; }
        public int SeatplanId { get; set; }
        public int VenueTypeId { get; set; }
        public int IsReserved { get; set; }
        public int Soldout { get; set; }
        public string? TicketSaleStop { get; set; }
    }
}
