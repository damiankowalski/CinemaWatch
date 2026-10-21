using System.Collections.ObjectModel;
using CinemaWatch.Core;

namespace CinemaWatch.ViewModels;

/// <summary>
/// Step 4: what will be watched, plus the profile actions: check now, save, copy the command line and
/// add to / remove from Task Scheduler.
/// </summary>
public sealed class SummaryStepViewModel : ObservableObject
{
    private readonly SeatAvailabilityService _service;
    private readonly IUserInteraction _ui;
    private readonly ITaskScheduler _scheduler;
    private readonly AppEnvironment _environment;
    private readonly Func<WatchProfile> _buildProfile;
    private readonly Func<DateTime> _clock;

    private string _conditionText = "";
    private string? _skippedText;
    private string _profileName = "";
    private string? _profilePath;
    private int _intervalMinutes = 10;
    private bool _isBusy;
    private string? _actionText;
    private bool _actionIsError;
    private string? _lastCheckText;
    private bool _isScheduled;
    private string _scheduleStatusText = "";

    public SummaryStepViewModel(
        SeatAvailabilityService service,
        IUserInteraction ui,
        ITaskScheduler scheduler,
        AppEnvironment environment,
        Func<WatchProfile> buildProfile,
        Func<DateTime> clock)
    {
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

    public IReadOnlyList<int> IntervalOptions { get; } = [5, 10, 15, 30, 60];

    public string ConditionText
    {
        get => _conditionText;
        private set => SetProperty(ref _conditionText, value);
    }

    public string? SkippedText
    {
        get => _skippedText;
        private set
        {
            if (SetProperty(ref _skippedText, value))
                OnPropertyChanged(nameof(HasSkipped));
        }
    }

    public bool HasSkipped => SkippedText is not null;

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

    public string ProfilePathText => ProfilePath ?? "Profil nie jest jeszcze zapisany.";

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

    public string? ActionText
    {
        get => _actionText;
        private set => SetProperty(ref _actionText, value);
    }

    public bool ActionIsError
    {
        get => _actionIsError;
        private set => SetProperty(ref _actionIsError, value);
    }

    public string? LastCheckText
    {
        get => _lastCheckText;
        private set => SetProperty(ref _lastCheckText, value);
    }

    public bool IsScheduled
    {
        get => _isScheduled;
        private set
        {
            if (SetProperty(ref _isScheduled, value))
                UnscheduleCommand.NotifyCanExecuteChanged();
        }
    }

    public string ScheduleStatusText
    {
        get => _scheduleStatusText;
        private set => SetProperty(ref _scheduleStatusText, value);
    }

    public string HintText =>
        "Profil zapisuje wybrane seanse i miejsca. Harmonogram zadań uruchamia CinemaWatch bez okna co wybrany czas " +
        "i pokazuje powiadomienie Windows, gdy zwolni się miejsce z Twojego zaznaczenia (działa, gdy jesteś zalogowany). " +
        "Po ostatnim seansie z profilu zadanie usuwa się samo.";

    // ------------------------------------------------------------------ loading

    public void Load(IReadOnlyList<ScreeningSeatsItem> items, int minAdjacent, string defaultProfileName)
    {
        Items.Clear();
        foreach (var item in items.Where(i => i.Map is not null && i.WatchedCount > 0))
        {
            var map = item.Map!;
            var available = SeatMatcher.FindAvailable(map.Seats, map.WatchedKeys, minAdjacent).Count;
            Items.Add(new SummaryItem(
                screening: item.Screening,
                rows: SeatSelectionText.Describe(map.Seats.Plan, map.WatchedKeys)
                    .Select(r => new SummaryRow($"Rząd {r.Row}", r.Text))
                    .ToList(),
                countsText: $"{Formatting.Count(map.WatchedCount, "miejsce", "miejsca", "miejsc")} · teraz wolnych: {available}",
                ui: _ui));
        }

        ConditionText = minAdjacent == 1
            ? "Powiadomienie: gdy zwolni się którekolwiek z zaznaczonych miejsc."
            : $"Powiadomienie: gdy wśród zaznaczonych zwolni się blok co najmniej {minAdjacent} sąsiednich miejsc.";

        var skipped = items.Count(i => i.Map is null || i.WatchedCount == 0);
        SkippedText = skipped == 0
            ? null
            : $"Pominięto {Formatting.Count(skipped, "seans", "seanse", "seansów")} bez zaznaczonych miejsc.";

        if (string.IsNullOrWhiteSpace(ProfileName))
            ProfileName = defaultProfileName;
        ActionText = null;
        LastCheckText = null;
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
        ActionText = null;
        LastCheckText = null;
        IsScheduled = false;
        ScheduleStatusText = "";
        RefreshCommands();
    }

    // ------------------------------------------------------------------ actions

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        ActionText = null;
        ActionIsError = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ReportError(ErrorMessages.Describe(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckNowAsync()
    {
        var result = await ProfileChecker.CheckAsync(_buildProfile(), _service, _clock());
        foreach (var check in result.Screenings)
            Items.FirstOrDefault(i => i.ScreeningId == check.Screening.Id)?.SetCheck(check);

        var withSeats = result.Screenings.Count(s => s.Available.Count > 0);
        LastCheckText = $"Sprawdzono o {result.CheckedAt:HH:mm:ss}. " + (withSeats == 0
            ? "Brak wolnych miejsc z zaznaczenia."
            : $"Wolne miejsca z zaznaczenia: {Formatting.Count(withSeats, "seans", "seanse", "seansów")}.");
    }

    private Task SaveAsync()
    {
        var path = _ui.PickProfileToSave(InitialDirectory(), SuggestedFileName());
        if (path is null) return Task.CompletedTask;
        Save(path);
        ActionText = $"Zapisano profil: {path}";
        return RefreshScheduleStatusAsync();
    }

    private Task CopyCommandLineAsync()
    {
        var path = EnsureSaved();
        if (path is null || _environment.ExePath is null) return Task.CompletedTask;
        _ui.CopyText(SchtasksScheduler.BuildTaskCommand(_environment.ExePath, path));
        ActionText = "Skopiowano polecenie do schowka.";
        return Task.CompletedTask;
    }

    private async Task ScheduleAsync()
    {
        var path = EnsureSaved();
        if (path is null) return;
        if (_environment.ExePath is null)
        {
            ReportError("Nie można ustalić ścieżki do CinemaWatch.exe.");
            return;
        }

        var taskName = SchtasksScheduler.TaskNameFor(path);
        var message =
            $"Zadanie: {taskName}\n" +
            $"Uruchamianie: co {IntervalMinutes} min, gdy jesteś zalogowany\n" +
            $"Polecenie: {SchtasksScheduler.BuildTaskCommand(_environment.ExePath, path)}\n\n" +
            (_environment.IsBuildOutput
                ? "Uwaga: aplikacja działa z folderu kompilacji (bin). Po przebudowaniu projektu ścieżka może się zmienić. " +
                  "Zalecane: zbuduj .\\publish.ps1 i dodaj zadanie z artifacts\\CinemaWatch.exe.\n\n"
                : "") +
            "Utworzyć zadanie?";
        if (!_ui.Confirm("Dodać do Harmonogramu zadań?", message)) return;

        var result = await _scheduler.RegisterAsync(taskName, _environment.ExePath, path, IntervalMinutes);
        if (result.Success) ActionText = result.Message;
        else ReportError(result.Message);
        await RefreshScheduleStatusAsync();
    }

    private async Task UnscheduleAsync()
    {
        if (ProfilePath is null) return;
        var taskName = SchtasksScheduler.TaskNameFor(ProfilePath);
        if (!_ui.Confirm("Usunąć z Harmonogramu zadań?", $"Zadanie {taskName} zostanie usunięte. Plik profilu zostaje.")) return;

        var result = await _scheduler.DeleteAsync(taskName);
        if (result.Success) ActionText = result.Message;
        else ReportError(result.Message);
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
    }

    private async Task RefreshScheduleStatusAsync()
    {
        if (ProfilePath is null)
        {
            IsScheduled = false;
            ScheduleStatusText = "Harmonogram: najpierw zapisz profil.";
            return;
        }

        var taskName = SchtasksScheduler.TaskNameFor(ProfilePath);
        try
        {
            IsScheduled = await _scheduler.ExistsAsync(taskName);
            ScheduleStatusText = IsScheduled ? $"Harmonogram: zadanie {taskName} jest aktywne." : "Harmonogram: brak zadania dla tego profilu.";
        }
        catch (Exception ex)
        {
            IsScheduled = false;
            ScheduleStatusText = $"Harmonogram: nie udało się sprawdzić ({ex.Message}).";
        }
    }

    private string InitialDirectory() =>
        ProfilePath is not null ? Path.GetDirectoryName(ProfilePath)! : _environment.ProfilesDirectory;

    private string SuggestedFileName() =>
        ProfileStore.ToFileName(string.IsNullOrWhiteSpace(ProfileName) ? "profil" : ProfileName) + ProfileStore.FileExtension;

    private void ReportError(string message)
    {
        ActionText = message;
        ActionIsError = true;
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

/// <summary>One watched screening in the summary, with the result of the last "check now".</summary>
public sealed class SummaryItem : ObservableObject
{
    private string? _resultText;
    private bool _isMatch;

    public SummaryItem(Screening screening, IReadOnlyList<SummaryRow> rows, string countsText, IUserInteraction ui)
    {
        ScreeningId = screening.Id;
        Title = $"{Formatting.DayHeader(DateOnly.FromDateTime(screening.StartsAt))}, {Formatting.Time(screening.StartsAt)}";
        Subtitle = $"{screening.Auditorium} · {Formatting.Languages(screening)}";
        Rows = rows;
        CountsText = countsText;
        BookingLink = screening.BookingLink;
        BuyCommand = new RelayCommand(() => ui.OpenUrl(BookingLink), () => !string.IsNullOrWhiteSpace(BookingLink));
    }

    public string ScreeningId { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public IReadOnlyList<SummaryRow> Rows { get; }
    public string CountsText { get; }
    public string BookingLink { get; }
    public RelayCommand BuyCommand { get; }

    public string? ResultText
    {
        get => _resultText;
        private set
        {
            if (SetProperty(ref _resultText, value))
                OnPropertyChanged(nameof(HasResult));
        }
    }

    public bool HasResult => ResultText is not null;

    public bool IsMatch
    {
        get => _isMatch;
        private set => SetProperty(ref _isMatch, value);
    }

    public void SetCheck(ScreeningCheck check)
    {
        IsMatch = check.Available.Count > 0;
        ResultText = check switch
        {
            { IsPast: true } => "Seans już się odbył.",
            { Error: not null } => $"Błąd: {check.Error}",
            _ when IsMatch => $"Wolne teraz: {NotificationBuilder.DescribeSeats(check.Available)}",
            _ => "Brak wolnych miejsc z zaznaczenia.",
        };
    }
}

public sealed record SummaryRow(string Row, string Text);
