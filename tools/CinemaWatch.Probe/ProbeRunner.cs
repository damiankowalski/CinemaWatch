using System.Diagnostics;
using System.Globalization;
using CinemaWatch.Core;

namespace CinemaWatch.Probe;

public static class ProbeExitCodes
{
    public const int Checked = 0;
    public const int Configuration = 1;
    public const int AllFailed = 2;
    public const int Blocked = 3;
    public const int NothingToCheck = 4;
}

/// <summary>
/// One probe run: read the profiles, check every watched screening once (KB §5.4: details + free seats, hall layout
/// cached for 7 days), log what would be e-mailed, fetch the repertoire of the watched cinemas once per interval,
/// write a summary. Read-only; sends no notifications. A scheduler repeats the run.
/// </summary>
public sealed class ProbeRunner(ProbeOptions options, HttpMessageHandler network, TimeProvider clock, TextWriter output, string userAgent)
{
    /// <summary>Timer jitter must not push the hourly repertoire request to every second hour.</summary>
    public static readonly TimeSpan RepertoireTolerance = TimeSpan.FromMinutes(5);

    public const string WouldNotifyFile = "would-notify.log";
    public const string RepertoireStampFile = "repertoire-last.txt";

    private string Data => options.DataDirectory;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var startedUtc = clock.GetUtcNow();
        var run = startedUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var now = PolishTime.Now(clock);

        var log = new RequestLog(Data);
        try
        {
            log.EnsureFile(startedUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"ERROR: cannot write to the data folder {Data}: {ex.Message}");
            return ProbeExitCodes.Configuration;
        }

        var profiles = LoadProfiles();
        if (profiles.Count == 0)
        {
            output.WriteLine($"ERROR: no readable *{ProfileStore.FileExtension} profiles in {options.ProfilesDirectory}");
            return ProbeExitCodes.Configuration;
        }

        var upcoming = profiles
            .SelectMany(p => p.Profile.Screenings.Where(s => s.StartsAt > now).Select(s => (p.Profile, Screening: s)))
            .ToList();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Run {run}: {profiles.Count} profile(s), {upcoming.Select(u => u.Screening.Id).Distinct().Count()} screening(s) to check, Polish time {now:yyyy-MM-dd HH:mm}, probe {ProbeInfo.Version}"));

        using var recorder = new RequestRecorder(network, disposeNetwork: false, log, clock, run);
        using var client = new CinemaCityClient(recorder, disposeHandler: false, userAgent);
        var service = new SeatAvailabilityService(client, Path.Combine(Data, "cache"));

        // Each screening is fetched once per run, even when several profiles watch it.
        var loads = new Dictionary<string, Task<ScreeningSeats>>(StringComparer.Ordinal);
        Task<ScreeningSeats> Load(string id, CancellationToken token)
        {
            if (!loads.TryGetValue(id, out var task))
                loads[id] = task = service.GetScreeningSeatsAsync(id, token);
            return task;
        }

        var checks = new List<ScreeningCheck>();
        var wouldNotify = 0;
        foreach (var (path, profile) in profiles)
        {
            var result = await ProfileChecker.CheckAsync(profile, Load, now, cancellationToken).ConfigureAwait(false);
            checks.AddRange(result.Screenings);
            foreach (var check in result.Screenings)
                output.WriteLine($"  [{profile.Name}] {Describe(check)}");

            var statePath = Path.Combine(Data, "state", ProfileStore.NameFromPath(path) + ".json");
            var state = NotificationState.Load(statePath);
            var news = state.Update(result);
            if (news.Count > 0)
            {
                wouldNotify++;
                var notification = NotificationBuilder.Build(profile, news);
                var text = $"[{profile.Name}] {notification.Title} | {string.Join(" | ", notification.Lines)} | {notification.LaunchUrl}";
                output.WriteLine($"  WOULD NOTIFY {text}");
                File.AppendAllText(Path.Combine(Data, WouldNotifyFile),
                    startedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "  " + text + "\n");
            }
            state.Save(statePath);
        }

        if (recorder.BlockedBy is null && upcoming.Count > 0)
            await CheckRepertoireAsync(client, upcoming, profiles, now, startedUtc, cancellationToken).ConfigureAwait(false);

        var exitCode = ExitCode(checks, recorder);
        if (recorder.BlockedBy is { } blocked)
        {
            var saved = SaveBlockedBody(run, recorder);
            output.WriteLine($"BLOCKED: {blocked.Describe()} - the run stopped, nothing more was sent"
                + (saved is null ? "" : $"; response saved to {saved}"));
        }
        if (exitCode == ProbeExitCodes.NothingToCheck)
            output.WriteLine("NOTHING TO CHECK: all screenings of all profiles are over - add profiles with upcoming screenings");

        var statuses = recorder.Records
            .GroupBy(r => r.Status?.ToString(CultureInfo.InvariantCulture) ?? "error")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} x{g.Count()}");
        var errors = checks.Count(c => c.Error is not null);
        var skipped = recorder.Skipped > 0 ? string.Create(CultureInfo.InvariantCulture, $", {recorder.Skipped} skipped after the block") : "";
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Done in {watch.Elapsed.TotalSeconds:0.0} s: {recorder.Records.Count} request(s) ({string.Join(", ", statuses)}){skipped}, {errors} screening error(s), {wouldNotify} would-notify, exit {exitCode}"));
        return exitCode;
    }

    private List<(string Path, WatchProfile Profile)> LoadProfiles()
    {
        var profiles = new List<(string, WatchProfile)>();
        if (!Directory.Exists(options.ProfilesDirectory))
            return profiles;

        foreach (var path in Directory.EnumerateFiles(options.ProfilesDirectory, "*" + ProfileStore.FileExtension).Order(StringComparer.Ordinal))
        {
            try
            {
                profiles.Add((path, ProfileStore.Load(path)));
            }
            catch (Exception ex)
            {
                output.WriteLine($"ERROR: cannot read profile {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return profiles;
    }

    /// <summary>
    /// The repertoire endpoint (www.cinema-city.pl) is what the browser wizard of MVP 1 will need. One request per
    /// watched cinema for today's Polish date, once per <see cref="ProbeOptions.RepertoireInterval"/>.
    /// </summary>
    private async Task CheckRepertoireAsync(
        CinemaCityClient client,
        List<(WatchProfile Profile, WatchedScreening Screening)> upcoming,
        List<(string Path, WatchProfile Profile)> profiles,
        DateTime now,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        if (options.RepertoireInterval <= TimeSpan.Zero)
            return;

        var stampPath = Path.Combine(Data, RepertoireStampFile);
        if (File.Exists(stampPath)
            && DateTimeOffset.TryParse(File.ReadAllText(stampPath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var last)
            && startedUtc - last < options.RepertoireInterval - Tolerance())
            return;

        var cinemas = upcoming
            .Select(u => string.IsNullOrWhiteSpace(u.Screening.CinemaId) ? u.Profile.CinemaId : u.Screening.CinemaId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var day = DateOnly.FromDateTime(now);
        foreach (var cinema in cinemas)
        {
            try
            {
                var repertoire = await client.GetRepertoireAsync(cinema, day, cancellationToken).ConfigureAwait(false);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  Repertoire {cinema} {day:yyyy-MM-dd}: {repertoire.Films.Count} film(s), {repertoire.Screenings.Count} screening(s)"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  Repertoire {cinema} {day:yyyy-MM-dd}: ERROR {ErrorMessages.Describe(ex)}"));
                if (ex is ProbeStoppedException || ex is CinemaCityException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests })
                    break;
            }
        }
        // Stamp even after errors: the next attempt follows the interval, not every run.
        File.WriteAllText(stampPath, startedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    private TimeSpan Tolerance() =>
        options.RepertoireInterval / 2 < RepertoireTolerance ? options.RepertoireInterval / 2 : RepertoireTolerance;

    private static int ExitCode(List<ScreeningCheck> checks, RequestRecorder recorder)
    {
        if (recorder.BlockedBy is not null)
            return ProbeExitCodes.Blocked;
        var live = checks.Where(c => !c.IsPast).ToList();
        if (live.Count == 0)
            return ProbeExitCodes.NothingToCheck;
        return live.All(c => c.Error is not null) ? ProbeExitCodes.AllFailed : ProbeExitCodes.Checked;
    }

    private static string Describe(ScreeningCheck check)
    {
        var s = check.Screening;
        var where = string.IsNullOrWhiteSpace(s.City) ? "" : s.City + " ";
        var when = where + s.StartsAt.ToString("ddd dd.MM HH:mm", CultureInfo.InvariantCulture) + $" ({s.Id})";
        return check switch
        {
            { IsPast: true } => $"{when}: over - skipped",
            { Error: not null } => $"{when}: ERROR {check.Error}",
            _ => string.Create(CultureInfo.InvariantCulture,
                $"{when}: {check.Available.Count} free watched seat(s) of {s.SeatKeys.Count} (hall {check.Seats?.FreeCount}/{check.Seats?.TotalCount} free)"),
        };
    }

    /// <summary>Keeps the blocked response (e.g. a Cloudflare challenge page) for diagnosis: blocked/{run}.html or .txt.</summary>
    private string? SaveBlockedBody(string run, RequestRecorder recorder)
    {
        if (recorder.BlockedBody is not { Length: > 0 } body)
            return null;
        try
        {
            var extension = recorder.BlockedBy?.ContentType switch
            {
                "text/html" => ".html",
                "application/json" => ".json",
                _ => ".txt",
            };
            var path = Path.Combine(Data, "blocked", run + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, body);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
