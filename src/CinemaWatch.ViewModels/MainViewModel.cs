using System.Collections.ObjectModel;
using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>Language choice in the drop-down (names are shown in their own language).</summary>
public sealed record LanguageOption(AppLanguage Language, string Name);

/// <summary>
/// Main window: profile tabs (each tab is a full wizard), opening / closing profiles, "check all", language.
/// At start-up the tabs are restored from the profiles folder (profiles whose screenings are all over are skipped).
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SessionServices _services;
    private readonly Action? _languageChanged;
    private ProfileSessionViewModel? _selectedSession;
    private bool _isCheckingAll;
    private Func<string>? _checkAllText;

    /// <param name="languageChanged">Called after the user switched the language (the app saves it and updates WPF).</param>
    public MainViewModel(
        CinemaCityClient client,
        IUserInteraction ui,
        ITaskScheduler scheduler,
        AppEnvironment environment,
        Func<DateTime>? clock = null,
        Action? languageChanged = null,
        IPreferences? preferences = null)
    {
        _services = new SessionServices(
            client,
            new SeatAvailabilityService(client, environment.CacheDirectory),
            ui,
            scheduler,
            environment,
            clock ?? (() => DateTime.Now),
            preferences ?? new InMemoryPreferences());
        _languageChanged = languageChanged;

        NewProfileCommand = new RelayCommand(() => SelectedSession = AddSession(new ProfileSessionViewModel(_services, closeRequested: Close)));
        OpenProfileCommand = new AsyncRelayCommand(OpenProfilesAsync);
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => !_isCheckingAll);

        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CheckAllLabel));
        SelectedSession = AddSession(new ProfileSessionViewModel(_services, closeRequested: Close));
        Localizer.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<ProfileSessionViewModel> Sessions { get; } = [];

    /// <summary>The visible tab. Showing a tab opened from a file loads it (once).</summary>
    public ProfileSessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value) || value is null) return;
            _ = value.EnsureLoadedAsync();
        }
    }

    public RelayCommand NewProfileCommand { get; }
    public AsyncRelayCommand OpenProfileCommand { get; }
    public AsyncRelayCommand CheckAllCommand { get; }

    /// <summary>"CinemaWatch 1.3.0"</summary>
    public string WindowTitle => $"CinemaWatch {AppInfo.Version}";

    public string CheckAllLabel => T($"Sprawdź wszystkie ({Sessions.Count})", $"Check all ({Sessions.Count})");

    public string? CheckAllText => _checkAllText?.Invoke();

    public bool HasUnsavedChanges => Sessions.Any(s => s.IsDirty);

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new LanguageOption(AppLanguage.Polish, "Polski"),
        new LanguageOption(AppLanguage.English, "English"),
    ];

    public LanguageOption SelectedLanguage
    {
        get => Languages.First(l => l.Language == Localizer.Language);
        set
        {
            if (value is not null)
                Localizer.SetLanguage(value.Language); // refresh happens in OnLanguageChanged
        }
    }

    /// <summary>
    /// Restores tabs from the profiles folder: every readable profile with at least one upcoming screening
    /// gets a tab (loaded when first shown). With none, the window keeps one empty "new profile" tab.
    /// </summary>
    public Task InitializeAsync()
    {
        var directory = _services.Environment.ProfilesDirectory;
        if (!Directory.Exists(directory)) return Task.CompletedTask;

        var now = _services.Clock();
        var restored = new List<ProfileSessionViewModel>();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + ProfileStore.FileExtension).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var profile = ProfileStore.Load(path);
                if (profile.Screenings.Any(s => s.StartsAt > now))
                    restored.Add(new ProfileSessionViewModel(_services, path, profile, Close));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Not a usable profile - skip it.
            }
        }
        if (restored.Count == 0) return Task.CompletedTask;

        // Replace the initial empty tab if the user has not touched it yet.
        var initial = Sessions.Count == 1 && !Sessions[0].IsDirty && Sessions[0].FilePath is null ? Sessions[0] : null;
        foreach (var session in restored)
            AddSession(session);
        SelectedSession = restored[0];
        if (initial is not null)
            RemoveSession(initial);
        return SelectedSession.EnsureLoadedAsync();
    }

    public void Dispose()
    {
        Localizer.LanguageChanged -= OnLanguageChanged;
        foreach (var session in Sessions)
            session.Dispose();
    }

    // ------------------------------------------------------------------ tabs

    private async Task OpenProfilesAsync()
    {
        var paths = _services.Ui.PickProfilesToOpen(SelectedSession?.FilePath is { } current
            ? Path.GetDirectoryName(current)!
            : _services.Environment.ProfilesDirectory);
        ProfileSessionViewModel? first = null;
        var toLoad = new List<ProfileSessionViewModel>();

        foreach (var path in paths)
        {
            // Already open: just switch to that tab.
            var existing = Sessions.FirstOrDefault(s => s.FilePath is not null && SamePath(s.FilePath, path));
            if (existing is not null)
            {
                first ??= existing;
                continue;
            }

            ProfileSessionViewModel session;
            try
            {
                session = new ProfileSessionViewModel(_services, path, ProfileStore.Load(path), Close);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Show the problem in a tab of its own, with the usual message.
                session = new ProfileSessionViewModel(_services, closeRequested: Close);
                await session.OpenAsync(path);
            }
            AddSession(session);
            toLoad.Add(session);
            first ??= session;
        }

        // An untouched empty tab is replaced by the opened profiles.
        if (first is not null && SelectedSession is { FilePath: null, IsDirty: false, StepIndex: 0 } empty && Sessions.Count > 1
            && !toLoad.Contains(empty))
            RemoveSession(empty);

        if (first is not null)
        {
            SelectedSession = first;
            await first.EnsureLoadedAsync();
        }
    }

    private void Close(ProfileSessionViewModel session)
    {
        if (session.IsDirty && !_services.Ui.Confirm(
                T("Niezapisane zmiany", "Unsaved changes"),
                T($"Profil „{session.Title}” ma niezapisane zmiany. Zamknąć go mimo to?",
                  $"The profile \"{session.Title}\" has unsaved changes. Close it anyway?")))
            return;

        var index = Sessions.IndexOf(session);
        RemoveSession(session);
        if (Sessions.Count == 0)
            AddSession(new ProfileSessionViewModel(_services, closeRequested: Close));
        if (ReferenceEquals(SelectedSession, session) || SelectedSession is null)
            SelectedSession = Sessions[Math.Clamp(index, 0, Sessions.Count - 1)];
    }

    private ProfileSessionViewModel AddSession(ProfileSessionViewModel session)
    {
        Sessions.Add(session);
        return session;
    }

    private void RemoveSession(ProfileSessionViewModel session)
    {
        Sessions.Remove(session);
        session.Dispose();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ check all

    /// <summary>
    /// Checks every tab that has selected seats - opened tabs with their current state, others from their file.
    /// Profiles are checked one after another, and a screening watched in several profiles is fetched only once.
    /// </summary>
    private async Task CheckAllAsync()
    {
        var targets = Sessions
            .Select(s => (Session: s, Profile: s.GetProfileForCheck()))
            .Where(t => t.Profile is { Screenings.Count: > 0 })
            .ToList();
        if (targets.Count == 0)
        {
            SetCheckAllText(() => T("Żaden profil nie ma jeszcze zaznaczonych miejsc.", "No profile has selected seats yet."));
            return;
        }

        _isCheckingAll = true;
        CheckAllCommand.NotifyCanExecuteChanged();
        SetCheckAllText(() => T("Sprawdzanie wszystkich profili…", "Checking all profiles…"));
        try
        {
            var fetched = new Dictionary<string, Task<ScreeningSeats>>(StringComparer.Ordinal);
            Task<ScreeningSeats> Fetch(string id, CancellationToken ct)
            {
                if (!fetched.TryGetValue(id, out var task))
                    fetched[id] = task = _services.SeatService.GetScreeningSeatsAsync(id, ct);
                return task;
            }

            var withSeats = 0;
            DateTime checkedAt = default;
            foreach (var (session, profile) in targets)
            {
                var result = await ProfileChecker.CheckAsync(profile!, Fetch, _services.Clock());
                session.ApplyCheckResult(result);
                checkedAt = result.CheckedAt;
                if (result.HasMatches) withSeats++;
            }

            var count = targets.Count;
            var time = checkedAt.ToString("HH:mm:ss", Formatting.Culture);
            SetCheckAllText(() =>
                T($"Sprawdzono o {time}: ", $"Checked at {time}: ")
                + Count(count, "profil", "profile", "profili", "profile", "profiles")
                + (withSeats == 0
                    ? T(", brak wolnych miejsc.", ", no free seats.")
                    : T($", wolne miejsca w {withSeats}.", $", free seats in {withSeats}.")));
        }
        finally
        {
            _isCheckingAll = false;
            CheckAllCommand.NotifyCanExecuteChanged();
        }
    }

    private void SetCheckAllText(Func<string>? text)
    {
        _checkAllText = text;
        OnPropertyChanged(nameof(CheckAllText));
    }

    // ------------------------------------------------------------------ language

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(CheckAllLabel));
        OnPropertyChanged(nameof(CheckAllText));
        _languageChanged?.Invoke();
    }
}
