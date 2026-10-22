using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Core;

/// <summary>
/// The "--profile" mode used by Task Scheduler: check once, notify about newly available seats, log, exit.
/// When every screening of the profile is over, the profile's scheduled task removes itself.
/// Exit codes: 0 = checked (with or without matches), 1 = profile could not be read, 2 = every screening failed.
/// </summary>
public sealed class HeadlessRunner(
    SeatAvailabilityService service,
    INotifier notifier,
    FileLog log,
    Func<DateTime>? clock = null,
    ITaskScheduler? scheduler = null)
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);

    public async Task<int> RunAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        WatchProfile profile;
        try
        {
            profile = ProfileStore.Load(profilePath);
        }
        catch (Exception ex)
        {
            log.Write(T($"BŁĄD: nie można wczytać profilu {profilePath}: {ex.Message}", $"ERROR: cannot read profile {profilePath}: {ex.Message}"));
            return 1;
        }

        var result = await ProfileChecker.CheckAsync(profile, service, _clock(), cancellationToken).ConfigureAwait(false);
        foreach (var check in result.Screenings)
        {
            var when = check.Screening.StartsAt.ToString("dd.MM HH:mm");
            log.Write(check switch
            {
                { IsPast: true } => $"[{profile.Name}] {when}: " + T("seans już się odbył - pomijam", "screening is over - skipped"),
                { Error: not null } => $"[{profile.Name}] {when}: " + T("BŁĄD", "ERROR") + $" {check.Error}",
                _ => $"[{profile.Name}] {when}: " + T("wolnych z zaznaczenia", "free watched seats") + $" {check.Available.Count}"
                     + (check.Seats is null ? "" : $" ({T("sala", "hall")}: {check.Seats.FreeCount}/{check.Seats.TotalCount})"),
            });
        }

        var statePath = AppPaths.StateFileFor(profilePath);
        var state = NotificationState.Load(statePath);
        var news = state.Update(result);
        if (news.Count > 0)
        {
            try
            {
                var notification = NotificationBuilder.Build(profile, news);
                notifier.Show(notification);
                log.Write($"[{profile.Name}] {T("Powiadomienie", "Notification")}: {notification.Title} | {string.Join(" | ", notification.Lines)}");
            }
            catch (Exception ex)
            {
                // Do not save the state: the next run will try to notify again.
                log.Write($"[{profile.Name}] {T("BŁĄD powiadomienia", "Notification ERROR")}: {ex.Message}");
                return result.AllFailed ? 2 : 0;
            }
        }
        state.Save(statePath);

        if (result.Screenings.All(s => s.IsPast))
            await RetireProfileAsync(profile, profilePath, statePath).ConfigureAwait(false);
        return result.AllFailed ? 2 : 0;
    }

    /// <summary>All screenings are over: remove the scheduled task (if any) and the notification state.</summary>
    private async Task RetireProfileAsync(WatchProfile profile, string profilePath, string statePath)
    {
        log.Write($"[{profile.Name}] {T("Wszystkie seanse z profilu już się odbyły.", "All screenings of the profile are over.")}");
        TryDelete(statePath);
        if (scheduler is null) return;

        var taskName = SchtasksScheduler.TaskNameFor(profilePath);
        try
        {
            if (!await scheduler.ExistsAsync(taskName).ConfigureAwait(false)) return;
            var removed = await scheduler.DeleteAsync(taskName).ConfigureAwait(false);
            log.Write(removed.Success
                ? $"[{profile.Name}] " + T($"Usunięto zadanie {taskName} z Harmonogramu - nie jest już potrzebne.", $"Removed task {taskName} from Task Scheduler - no longer needed.")
                : $"[{profile.Name}] " + T($"Nie udało się usunąć zadania {taskName}: {removed.Message}", $"Could not remove task {taskName}: {removed.Message}"));
        }
        catch (Exception ex)
        {
            log.Write($"[{profile.Name}] " + T($"Nie udało się usunąć zadania {taskName}: {ex.Message}", $"Could not remove task {taskName}: {ex.Message}"));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Accepts "--profile path" and "--profile=path".</summary>
    public static string? GetProfileArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
                return args[i]["--profile=".Length..].Trim('"');
            if (string.Equals(args[i], "--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                return args[i + 1];
        }
        return null;
    }
}
