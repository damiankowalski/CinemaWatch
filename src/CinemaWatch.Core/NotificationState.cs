using System.Text.Json;

namespace CinemaWatch.Core;

/// <summary>
/// Remembers which watched seats were available at the last check, per screening, so a notification is shown
/// only for seats that became free since then (a seat that is taken and freed again notifies again).
/// </summary>
public sealed class NotificationState
{
    public Dictionary<string, List<string>> Available { get; set; } = [];

    /// <summary>
    /// Updates the state with a check result and returns the newly available seats per screening.
    /// Screenings that failed keep their previous state.
    /// </summary>
    public IReadOnlyList<(WatchedScreening Screening, IReadOnlyList<Seat> NewSeats)> Update(ProfileCheckResult result)
    {
        var news = new List<(WatchedScreening, IReadOnlyList<Seat>)>();
        foreach (var check in result.Screenings)
        {
            var id = check.Screening.Id;
            if (check.IsPast)
            {
                Available.Remove(id);
                continue;
            }
            if (check.Error is not null)
                continue;

            var previous = Available.TryGetValue(id, out var list) ? new HashSet<string>(list, StringComparer.Ordinal) : [];
            var fresh = check.Available.Where(s => !previous.Contains(s.Key)).ToList();
            if (fresh.Count > 0)
                news.Add((check.Screening, fresh));
            Available[id] = check.Available.Select(s => s.Key).ToList();
        }
        return news;
    }

    public static NotificationState Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<NotificationState>(File.ReadAllText(path)) ?? new NotificationState()
                : new NotificationState();
        }
        catch (JsonException)
        {
            return new NotificationState();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, overwrite: true);
    }
}
