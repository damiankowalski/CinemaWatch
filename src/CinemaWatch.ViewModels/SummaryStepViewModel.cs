using System.Collections.ObjectModel;
using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>
/// Step 4: what will be watched (one collapsible card per screening), plus the profile actions:
/// check now, save, copy the command line and add to / remove from Task Scheduler.
/// </summary>
public sealed class SummaryStepViewModel : ObservableObject
{
    private enum ScheduleState
    {
        Unknown,
        NotSaved,
        Scheduled,
        NotScheduled,
        QueryFailed,
    }

    private readonly SeatAvailabilityService _service;
    private readonly IUserInteraction _ui;
    private readonly ITaskScheduler _scheduler;
    private readonly AppEnvironment _environment;
    private readonly Func<WatchProfile> _buildProfile;
    private readonly Func<DateTime> _clock;
    private readonly Action<ProfileCheckResult>? _onChecked;
    private readonly Action<string>? _onSaved;

    private int _minAdjacent = 1;
    private int _skipped;
    private string _profileName = "";
    private string? _profilePath;
    private int _intervalMinutes = 10;
    private bool _isBusy;
    private Func<string>? _actionText;
    private bool _actionIsError;
    private ProfileCheckResult? _lastCheck;
    private ScheduleState _scheduleState = ScheduleState.Unknown;
    private string? _scheduleError;

    public SummaryStepViewModel(
        SeatAvailabilityService service,
        IUserInteraction ui,
        ITaskScheduler scheduler,
        AppEnvironment environment,
        Func<WatchProfile> buildProfile,
        Func<DateTime> clock,
        Action<ProfileCheckResult>? onChecked = null,
        Action<string>? onSaved = null)
    {
        _onChecked = onChecked;
        _onSaved = onSaved;
        _service = service;
        _ui = ui;
        _scheduler = scheduler;
        _environment = environment;
        _buildProfile = buildProfile;
        _clock = clock;

        CheckNowCommand = new AsyncRelayCommand(() => RunAsync(CheckNowAsync), () => !IsBusy && Items.Count > 0);
        SaveCommand = new AsyncRelayCommand(() => RunAsync(SaveAsync), () => !IsBusy && Items.Count > 0);
        CopyCommandLineCommand = new AsyncRelayCommand(() => RunAsync(CopyCommandLineAsync), () => !IsBusy && Items.Count > 0);
        ScheduleCommand = new AsyncRelayCommand(() => RunAsync(ScheduleAsync), () => !IsBusy && Items.Count > 0);
        UnscheduleCommand = new AsyncRelayCommand(() => RunAsync(UnscheduleAsync), () => !IsBusy && IsScheduled);
    }

    public ObservableCollection<SummaryItem> Items { get; } = [];

    public AsyncRelayCommand CheckNowCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand CopyCommandLineCommand { get; }
    public AsyncRelayCommand ScheduleCommand { get; }
    public AsyncRelayCommand UnscheduleCommand { get; }

    private static readonly IReadOnlyList<int> IntervalChoices = [5, 10, 15, 30, 60];

    /// <summary>Shared by all tabs (see <see cref="DatesStepViewModel.Cities"/>).</summary>
    public IReadOnlyList<int> IntervalOptions => IntervalChoices;

    public string ConditionText => _minAdjacent == 1
        ? T("Powiadomienie: gdy zwolni się którekolwiek z zaznaczonych miejsc.",
            "Notification: when any of the selected seats becomes free.")
        : T($"Powiadomienie: gdy wśród zaznaczonych zwolni się blok co najmniej {_minAdjacent} sąsiednich miejsc.",
            $"Notification: when at least {_minAdjacent} selected seats next to each other become free.");

    public string? SkippedText => _skipped == 0
        ? null
        : T("Pominięto ", "Skipped ") + Count(_skipped, "seans", "seanse", "seansów", "screening", "screenings")
          + T(" bez zaznaczonych miejsc.", " without selected seats.");

    public bool HasSkipped => _skipped > 0;

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value ?? "");
    }

    public string? ProfilePath
    {
        get => _profilePath;
        private set
        {
            if (SetProperty(ref _profilePath, value))
                OnPropertyChanged(nameof(ProfilePathText));
        }
    }

    public string ProfilePathText => ProfilePath ?? T("Profil nie jest jeszcze zapisany.", "The profile has not been saved yet.");

    public int IntervalMinutes
    {
        get => _intervalMinutes;
        set => SetProperty(ref _intervalMinutes, IntervalOptions.Contains(value) ? value : 10);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RefreshCommands();
        }
    }

    public string? ActionText => _actionText?.Invoke();

    public bool ActionIsError
    {
        get => _actionIsError;
        private set => SetProperty(ref _actionIsError, value);
    }

    public string? LastCheckText
    {
        get
        {
            if (_lastCheck is null) return null;
            var time = _lastCheck.CheckedAt.ToString("HH:mm:ss", Formatting.Culture);
            var withSeats = _lastCheck.Screenings.Count(s => s.Available.Count > 0);
            return withSeats == 0
                ? T($"Sprawdzono o {time}. Brak wolnych miejsc z zaznaczenia.", $"Checked at {time}. None of the selected seats are free.")
                : T($"Sprawdzono o {time}. Wolne miejsca z zaznaczenia: ", $"Checked at {time}. Selected seats free in ")
                  + Count(withSeats, "seans", "seanse", "seansów", "screening", "screenings") + ".";
        }
    }

    public bool IsScheduled => _scheduleState == ScheduleState.Scheduled;

    public string ScheduleStatusText
    {
        get
        {
            var task = ProfilePath is null ? "" : SchtasksScheduler.TaskNameFor(ProfilePath);
            return _scheduleState switch
            {
                ScheduleState.NotSaved => T("Harmonogram: najpierw zapisz profil.", "Task Scheduler: save the profile first."),
                ScheduleState.Scheduled => T($"Harmonogram: zadanie {task} jest aktywne.", $"Task Scheduler: task {task} is active."),
                ScheduleState.NotScheduled => T("Harmonogram: brak zadania dla tego profilu.", "Task Scheduler: no task for this profile."),
                ScheduleState.QueryFailed => T($"Harmonogram: nie udało się sprawdzić ({_scheduleError}).", $"Task Scheduler: could not check ({_scheduleError})."),
                _ => "",
            };
        }
    }

    public string HintText => T(
        "Profil zapisuje wybrane seanse i miejsca. Harmonogram zadań uruchamia CinemaWatch bez okna co wybrany czas " +
        "i pokazuje powiadomienie Windows, gdy zwolni się miejsce z Twojego zaznaczenia (działa, gdy jesteś zalogowany). " +
        "Po ostatnim seansie z profilu zadanie usuwa się samo.",
        "A profile stores the selected screenings and seats. Task Scheduler runs CinemaWatch without a window at the " +
        "chosen interval and shows a Windows notification when one of your selected seats becomes free (while you are " +
        "signed in). After the profile's last screening the task removes itself.");

    // ------------------------------------------------------------------ loading

    public void Load(IReadOnlyList<ScreeningSeatsItem> items, int minAdjacent, string defaultProfileName)
    {
        Items.Clear();
        foreach (var item in items.Where(i => i.Map is not null && i.WatchedCount > 0))
        {
            var map = item.Map!;
            Items.Add(new SummaryItem(
                item.Screening,
                map.Seats.Plan,
                new HashSet<string>(map.WatchedKeys, StringComparer.Ordinal),
                SeatMatcher.FindAvailable(map.Seats, map.WatchedKeys, minAdjacent).Count,
                _ui));
        }

        _minAdjacent = minAdjacent;
        _skipped = items.Count(i => i.Map is null || i.WatchedCount == 0);
        if (string.IsNullOrWhiteSpace(ProfileName))
            ProfileName = defaultProfileName;
        SetAction(null);
        _lastCheck = null;
        OnPropertyChanged(nameof(ConditionText));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(LastCheckText));
        RefreshCommands();
        _ = RefreshScheduleStatusAsync();
    }

    /// <summary>Called when a profile was opened from disk.</summary>
    public void SetProfile(string path, string name, int intervalMinutes)
    {
        ProfilePath = path;
        ProfileName = name;
        IntervalMinutes = intervalMinutes;
    }

    public void Reset()
    {
        Items.Clear();
        ProfilePath = null;
        ProfileName = "";
        IntervalMinutes = 10;
        SetAction(null);
        _lastCheck = null;
        _skipped = 0;
        SetScheduleState(ScheduleState.Unknown);
        OnPropertyChanged(nameof(LastCheckText));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(HasSkipped));
        RefreshCommands();
    }

    public void RefreshTexts()
    {
        foreach (var item in Items)
            item.RefreshTexts();
        foreach (var name in new[]
                 {
                     nameof(ConditionText), nameof(SkippedText), nameof(ProfilePathText), nameof(ActionText),
                     nameof(LastCheckText), nameof(ScheduleStatusText), nameof(HintText),
                 })
            OnPropertyChanged(name);
    }

    // ------------------------------------------------------------------ actions

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        SetAction(null);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ReportError(() => ErrorMessages.Describe(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckNowAsync()
    {
        var result = await ProfileChecker.CheckAsync(_buildProfile(), _service, _clock());
        if (_onChecked is not null) _onChecked(result); // the tab updates its badge and calls ShowCheckResult
        else ShowCheckResult(result);
    }

    /// <summary>Shows a check result (from "check now" or "check all") on the cards.</summary>
    public void ShowCheckResult(ProfileCheckResult result)
    {
        foreach (var check in result.Screenings)
            Items.FirstOrDefault(i => i.ScreeningId == check.Screening.Id)?.SetCheck(check);
        _lastCheck = result;
        OnPropertyChanged(nameof(LastCheckText));
    }

    private Task SaveAsync()
    {
        var path = _ui.PickProfileToSave(InitialDirectory(), SuggestedFileName());
        if (path is null) return Task.CompletedTask;
        Save(path);
        SetAction(() => T($"Zapisano profil: {path}", $"Profile saved: {path}"));
        return RefreshScheduleStatusAsync();
    }

    private Task CopyCommandLineAsync()
    {
        var path = EnsureSaved();
        if (path is null || _environment.ExePath is null) return Task.CompletedTask;
        _ui.CopyText(SchtasksScheduler.BuildTaskCommand(_environment.ExePath, path));
        SetAction(() => T("Skopiowano polecenie do schowka.", "Command copied to the clipboard."));
        return Task.CompletedTask;
    }

    private async Task ScheduleAsync()
    {
        var path = EnsureSaved();
        if (path is null) return;
        if (_environment.ExePath is null)
        {
            ReportError(() => T("Nie można ustalić ścieżki do CinemaWatch.exe.", "Cannot determine the path of CinemaWatch.exe."));
            return;
        }

        var taskName = SchtasksScheduler.TaskNameFor(path);
        var command = SchtasksScheduler.BuildTaskCommand(_environment.ExePath, path);
        var message =
            T($"Zadanie: {taskName}\nUruchamianie: co {IntervalMinutes} min, gdy jesteś zalogowany\nPolecenie: {command}\n\n",
              $"Task: {taskName}\nRuns: every {IntervalMinutes} min while you are signed in\nCommand: {command}\n\n") +
            (_environment.IsBuildOutput
                ? T("Uwaga: aplikacja działa z folderu kompilacji (bin). Po przebudowaniu projektu ścieżka może się zmienić. " +
                    "Zalecane: zbuduj .\\publish.ps1 i dodaj zadanie z artifacts\\CinemaWatch.exe.\n\n",
                    "Note: the app runs from a build folder (bin). The path may change after a rebuild. " +
                    "Recommended: build with .\\publish.ps1 and add the task from artifacts\\CinemaWatch.exe.\n\n")
                : "") +
            T("Utworzyć zadanie?", "Create the task?");
        if (!_ui.Confirm(T("Dodać do Harmonogramu zadań?", "Add to Task Scheduler?"), message)) return;

        var result = await _scheduler.RegisterAsync(taskName, _environment.ExePath, path, IntervalMinutes);
        ShowResult(result);
        await RefreshScheduleStatusAsync();
    }

    private async Task UnscheduleAsync()
    {
        if (ProfilePath is null) return;
        var taskName = SchtasksScheduler.TaskNameFor(ProfilePath);
        if (!_ui.Confirm(T("Usunąć z Harmonogramu zadań?", "Remove from Task Scheduler?"),
                T($"Zadanie {taskName} zostanie usunięte. Plik profilu zostaje.", $"Task {taskName} will be removed. The profile file stays.")))
            return;

        ShowResult(await _scheduler.DeleteAsync(taskName));
        await RefreshScheduleStatusAsync();
    }

    /// <summary>Saves to the known path (overwriting with the current selection) or asks where to save.</summary>
    private string? EnsureSaved()
    {
        var path = ProfilePath ?? _ui.PickProfileToSave(InitialDirectory(), SuggestedFileName());
        if (path is not null)
            Save(path);
        return path;
    }

    private void Save(string path)
    {
        var profile = _buildProfile();
        ProfileStore.Save(path, profile);
        ProfilePath = path;
        ProfileName = profile.Name;
        _onSaved?.Invoke(path);
    }

    private async Task RefreshScheduleStatusAsync()
    {
        if (ProfilePath is null)
        {
            SetScheduleState(ScheduleState.NotSaved);
            return;
        }

        try
        {
            var exists = await _scheduler.ExistsAsync(SchtasksScheduler.TaskNameFor(ProfilePath));
            SetScheduleState(exists ? ScheduleState.Scheduled : ScheduleState.NotScheduled);
        }
        catch (Exception ex)
        {
            _scheduleError = ex.Message;
            SetScheduleState(ScheduleState.QueryFailed);
        }
    }

    private void SetScheduleState(ScheduleState state)
    {
        _scheduleState = state;
        OnPropertyChanged(nameof(IsScheduled));
        OnPropertyChanged(nameof(ScheduleStatusText));
        UnscheduleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Scheduler results come already translated; they stay in the language they were produced in.</summary>
    private void ShowResult(SchedulerResult result)
    {
        var message = result.Message;
        if (result.Success) SetAction(() => message);
        else ReportError(() => message);
    }

    private string InitialDirectory() =>
        ProfilePath is not null ? Path.GetDirectoryName(ProfilePath)! : _environment.ProfilesDirectory;

    private string SuggestedFileName() =>
        ProfileStore.ToFileName(string.IsNullOrWhiteSpace(ProfileName) ? T("profil", "profile") : ProfileName) + ProfileStore.FileExtension;

    private void SetAction(Func<string>? text)
    {
        _actionText = text;
        ActionIsError = false;
        OnPropertyChanged(nameof(ActionText));
    }

    private void ReportError(Func<string> text)
    {
        _actionText = text;
        ActionIsError = true;
        OnPropertyChanged(nameof(ActionText));
    }

    private void RefreshCommands()
    {
        CheckNowCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        CopyCommandLineCommand.NotifyCanExecuteChanged();
        ScheduleCommand.NotifyCanExecuteChanged();
        UnscheduleCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// One watched screening in the summary. Collapsed by default (date, hall, counts); expanding shows the
/// selected seats row by row. The result of the last "check now" is visible in both states.
/// </summary>
public sealed class SummaryItem : ObservableObject
{
    private readonly Screening _screening;
    private readonly SeatPlan _plan;
    private readonly IReadOnlySet<string> _watched;
    private readonly int _availableAtLoad;
    private ScreeningCheck? _check;
    private bool _isExpanded;

    public SummaryItem(Screening screening, SeatPlan plan, IReadOnlySet<string> watched, int availableAtLoad, IUserInteraction ui)
    {
        _screening = screening;
        _plan = plan;
        _watched = watched;
        _availableAtLoad = availableAtLoad;
        BuyCommand = new RelayCommand(() => ui.OpenUrl(BookingLink), () => !string.IsNullOrWhiteSpace(BookingLink));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string ScreeningId => _screening.Id;
    public string BookingLink => _screening.BookingLink;
    public RelayCommand BuyCommand { get; }
    public RelayCommand ToggleExpandedCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public string Title => $"{Formatting.DayHeader(DateOnly.FromDateTime(_screening.StartsAt))}, {Formatting.Time(_screening.StartsAt)}";
    public string Subtitle => $"{_screening.Auditorium} · {Formatting.Languages(_screening)}";

    public string CountsText =>
        Count(_watched.Count, "miejsce", "miejsca", "miejsc", "seat", "seats") + " · " + T("teraz wolnych", "free now") + $": {_availableAtLoad}";

    public IReadOnlyList<SummaryRow> Rows => SeatSelectionText.Describe(_plan, _watched)
        .Select(r => new SummaryRow($"{T("Rząd", "Row")} {r.Row}", r.Text))
        .ToList();

    public string? ResultText => _check switch
    {
        null => null,
        { IsPast: true } => T("Seans już się odbył.", "The screening is over."),
        { Error: not null } c => $"{T("Błąd", "Error")}: {c.Error}",
        { Available.Count: > 0 } c => $"{T("Wolne teraz", "Free now")}: {NotificationBuilder.DescribeSeats(c.Available)}",
        _ => T("Brak wolnych miejsc z zaznaczenia.", "None of the selected seats are free."),
    };

    public bool HasResult => _check is not null;
    public bool IsMatch => _check is { Available.Count: > 0 };

    public void SetCheck(ScreeningCheck check)
    {
        _check = check;
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(IsMatch));
    }

    public void RefreshTexts()
    {
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(CountsText), nameof(Rows), nameof(ResultText) })
            OnPropertyChanged(name);
    }
}

public sealed record SummaryRow(string Row, string Text);
