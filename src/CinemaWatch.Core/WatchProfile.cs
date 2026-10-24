using System.Text.Encodings.Web;
using System.Text.Json;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Core;

/// <summary>
/// A saved monitoring setup: which screenings and which seats to watch. Stored as readable JSON so it can be
/// passed to <c>CinemaWatch.exe --profile &lt;path&gt;</c> (Task Scheduler) or reopened in the wizard.
/// </summary>
public sealed class WatchProfile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "";
    /// <summary>Main cinema (first of <see cref="CinemaIds"/>); kept so that version 1.3 can still open the profile.</summary>
    public string CinemaId { get; set; } = "";
    public string CinemaName { get; set; } = "";

    /// <summary>Cinemas the wizard searched (one city or all IMAX cinemas). Empty in profiles from before 1.4.</summary>
    public List<string> CinemaIds { get; set; } = [];
    public string FilmId { get; set; } = "";
    public string FilmName { get; set; } = "";
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }

    /// <summary>Notify only when at least this many watched seats are free next to each other.</summary>
    public int MinAdjacent { get; set; } = 1;

    /// <summary>Check interval used when the profile is added to Task Scheduler.</summary>
    public int IntervalMinutes { get; set; } = 10;

    public List<WatchedScreening> Screenings { get; set; } = [];
}

public sealed class WatchedScreening
{
    public string Id { get; set; } = "";
    public string CinemaId { get; set; } = "";
    public string City { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public string Auditorium { get; set; } = "";
    public string BookingLink { get; set; } = "";
    public List<string> SeatKeys { get; set; } = [];
}

public static class ProfileStore
{
    public const string FileExtension = ".cinemawatch.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Polish letters readable
    };

    public static WatchProfile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<WatchProfile>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException(T("Plik profilu jest pusty.", "The profile file is empty."));
        if (profile.Version > WatchProfile.CurrentVersion)
            throw new InvalidDataException(T(
                $"Profil zapisano w nowszej wersji aplikacji (wersja {profile.Version}).",
                $"The profile was saved by a newer version of the app (version {profile.Version})."));
        if (string.IsNullOrWhiteSpace(profile.CinemaId) || profile.Screenings.Count == 0)
            throw new InvalidDataException(T("Profil nie zawiera kina albo seansów.", "The profile has no cinema or screenings."));
        return profile;
    }

    /// <summary>Writes atomically (temp file + replace), so a crash never leaves a half-written profile.</summary>
    public static void Save(string path, WatchProfile profile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>"C:\x\Diuna IMAX.cinemawatch.json" → "Diuna IMAX"</summary>
    public static string NameFromPath(string path)
    {
        var file = Path.GetFileName(path);
        return file.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)
            ? file[..^FileExtension.Length]
            : Path.GetFileNameWithoutExtension(file);
    }

    /// <summary>Removes characters that are not allowed in file names.</summary>
    public static string ToFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var cleaned = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? T("profil", "profile") : cleaned;
    }
}
