using System.Text.Json;
using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>Language choice in the drop-down (names are shown in their own language).</summary>
public sealed record LanguageOption(AppLanguage Language, string Name);

/// <summary>Wizard shell: step navigation, busy state, errors, language, and opening / starting profiles.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int DatesIndex = 0;
    private const int ScreeningsIndex = 1;
    private const int SeatsIndex = 2;
    private const int SummaryIndex = 3;
    private const int MaxParallelRequests = 3;

    private readonly CinemaCityClient _client;
    private readonly SeatAvailabilityService _seatService;
    private readonly IUserInteraction _ui;
    private readonly ITaskScheduler _scheduler;
    private readonly AppEnvironment _environment;
    private readonly Func<DateTime> _clock;
    private readonly Action? _languageChanged;

    private int _stepIndex;
    private object _currentPage;
    private bool _isBusy;
    // Messages are stored as factories, so they re-render when the language changes.
    private Func<string?>? _statusText;
    private Func<string?>? _errorText;
    private CancellationTokenSource? _loadCts;

    /// <param name="languageChanged">Called after the user switched the language (the app saves it and updates WPF).</param>
    public MainViewModel(
        CinemaCityClient client,
        IUserInteraction ui,
        ITaskScheduler scheduler,
        AppEnvironment environment,
        Func<DateTime>? clock = null,
        Action? languageChanged = null)
    {
        _client = client;
        _seatService = new SeatAvailabilityService(client, environment.CacheDirectory);
        _ui = ui;
        _scheduler = scheduler;
        _environment = environment;
        _clock = clock ?? (() => DateTime.Now);
        _languageChanged = languageChanged;

        DatesStep = new DatesStepViewModel();
        ScreeningsStep = new ScreeningsStepViewModel();
        SeatsStep = new SeatsStepViewModel();
        SummaryStep = new SummaryStepViewModel(_seatService, ui, scheduler, environment, BuildProfile, _clock);

        Steps =
        [
            new StepItem(1, "Kino i termin", "Cinema and dates"),
            new StepItem(2, "Film i seanse", "Film and screenings"),
            new StepItem(3, "Miejsca", "Seats"),
            new StepItem(4, "Podsumowanie", "Summary"),
        ];

        NextCommand = new AsyncRelayCommand(GoNextAsync, () => !IsBusy && _stepIndex < SummaryIndex);
        BackCommand = new RelayCommand(GoBack, () => CanGoBack);
        NewProfileCommand = new RelayCommand(StartNewProfile, () => !IsBusy);
        OpenProfileCommand = new AsyncRelayCommand(OpenProfileAsync, () => !IsBusy);

        _currentPage = DatesStep;
        UpdateSteps();
        Localizer.LanguageChanged += OnLanguageChanged;
    }

    public DatesStepViewModel DatesStep { get; }
    public ScreeningsStepViewModel ScreeningsStep { get; }
    public SeatsStepViewModel SeatsStep { get; }
    public SummaryStepViewModel SummaryStep { get; }

    public IReadOnlyList<StepItem> Steps { get; }

    public AsyncRelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public AsyncRelayCommand OpenProfileCommand { get; }

    /// <summary>"CinemaWatch 1.0.0"</summary>
    public string WindowTitle => $"CinemaWatch {AppInfo.Version}";

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

    public int StepIndex => _stepIndex;

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
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

    public string? StatusText => _statusText?.Invoke();

    public string? ErrorText => _errorText?.Invoke();

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool CanGoBack => _stepIndex > DatesIndex;

    public bool IsLastStep => _stepIndex == SummaryIndex;

    public void Dispose()
    {
        Localizer.LanguageChanged -= OnLanguageChanged;
        _loadCts?.Dispose();
    }

    // ------------------------------------------------------------------ language

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var step in Steps) step.RefreshTexts();
        DatesStep.RefreshTexts();
        ScreeningsStep.RefreshTexts();
        SeatsStep.RefreshTexts();
        SummaryStep.RefreshTexts();
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        _languageChanged?.Invoke();
    }

    private void SetStatus(Func<string?>? text)
    {
        _statusText = text;
        OnPropertyChanged(nameof(StatusText));
    }

    private void SetError(Func<string?>? text)
    {
        _errorText = text;
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
    }

    // ------------------------------------------------------------------ profile

    /// <summary>Builds a profile from the current wizard state.</summary>
    public WatchProfile BuildProfile()
    {
        var dates = DatesStep.GetDates();
        var film = ScreeningsStep.SelectedFilm;
        return new WatchProfile
        {
            Name = string.IsNullOrWhiteSpace(SummaryStep.ProfileName) ? DefaultProfileName() : SummaryStep.ProfileName.Trim(),
            CinemaId = DatesStep.Cinema.Id,
            CinemaName = DatesStep.Cinema.Name,
            FilmId = film?.FilmId ?? "",
            FilmName = film?.Name ?? "",
            DateFrom = dates.Count > 0 ? dates[0] : DateOnly.FromDateTime(_clock()),
            DateTo = dates.Count > 0 ? dates[^1] : DateOnly.FromDateTime(_clock()),
            MinAdjacent = SeatsStep.MinAdjacent,
            IntervalMinutes = SummaryStep.IntervalMinutes,
            Screenings = SeatsStep.Items
                .Where(i => i.Map is not null && i.WatchedCount > 0)
                .Select(i => new WatchedScreening
                {
                    Id = i.Screening.Id,
                    StartsAt = i.Screening.StartsAt,
                    Auditorium = i.Screening.Auditorium,
                    BookingLink = i.Screening.BookingLink,
                    SeatKeys = i.Map!.WatchedKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                })
                .ToList(),
        };
    }

    private string DefaultProfileName()
    {
        var dates = DatesStep.GetDates();
        var film = ScreeningsStep.SelectedFilm?.Name ?? T("Profil", "Profile");
        if (dates.Count == 0) return film;
        var from = Formatting.ShortDate(dates[0]);
        var to = Formatting.ShortDate(dates[^1]);
        return dates.Count == 1 ? $"{film} {from}" : $"{film} {from}–{to}";
    }

    private void StartNewProfile()
    {
        _loadCts?.Cancel();
        DatesStep.Reset();
        ScreeningsStep.Load([], ScreeningFilter.ImaxWithoutDubbing, _clock());
        SeatsStep.Reset();
        SummaryStep.Reset();
        SetError(null);
        SetStatus(null);
        GoTo(DatesIndex);
    }

    private async Task OpenProfileAsync()
    {
        var path = _ui.PickProfileToOpen(_environment.ProfilesDirectory);
        if (path is null) return;

        WatchProfile profile;
        try
        {
            profile = ProfileStore.Load(path);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            var file = Path.GetFileName(path);
            SetError(ex is JsonException
                ? () => T($"Nie udało się otworzyć profilu: plik {file} nie jest poprawnym profilem CinemaWatch.",
                          $"Could not open the profile: {file} is not a valid CinemaWatch profile.")
                : () => T($"Nie udało się otworzyć profilu: {ex.Message}", $"Could not open the profile: {ex.Message}"));
            return;
        }

        try
        {
            await ReplayProfileAsync(profile, path);
        }
        catch (OperationCanceledException) when (_loadCts?.IsCancellationRequested == true)
        {
        }
        catch (Exception ex)
        {
            SetError(() => T("Nie udało się otworzyć profilu: ", "Could not open the profile: ") + ErrorMessages.Describe(ex));
            SetStatus(null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens a profile by replaying the wizard with its data: fresh repertoire, fresh occupancy, the saved seat
    /// selection - and ends on the summary. Every step stays editable afterwards.
    /// </summary>
    private async Task ReplayProfileAsync(WatchProfile profile, string path)
    {
        StartNewProfile();
        var now = _clock();
        if (profile.Screenings.All(s => s.StartsAt <= now))
        {
            await RetireExpiredProfileAsync(path);
            return;
        }

        var from = profile.DateFrom.ToDateTime(TimeOnly.MinValue);
        if (from < now.Date) from = now.Date;
        var to = profile.DateTo.ToDateTime(TimeOnly.MinValue);
        DatesStep.DateFrom = from;
        DatesStep.IsRange = to > from;
        if (to > from) DatesStep.DateTo = to;

        if (!await LeaveDatesStepAsync()) return;

        var missing = ScreeningsStep.SelectScreenings(profile.FilmId, profile.Screenings.Select(s => s.Id));
        if (ScreeningsStep.SelectedCount == 0)
        {
            SetError(() => T("W repertuarze nie ma już seansów z tego profilu.", "The repertoire no longer has any screenings from this profile."));
            return;
        }

        SeatsStep.MinAdjacent = profile.MinAdjacent;
        SeatsStep.SetSelections(profile.Screenings.Select(s => (s.Id, (IEnumerable<string>)s.SeatKeys)));
        if (!await LeaveScreeningsStepAsync()) return;

        SummaryStep.SetProfile(path, profile.Name, profile.IntervalMinutes);
        if (!LeaveSeatsStep()) return;

        var name = profile.Name;
        SetStatus(missing > 0
            ? () => T("Otwarto profil. Pominięto ", "Profile opened. Skipped ")
                    + Count(missing, "seans", "seanse", "seansów", "screening", "screenings")
                    + T(", których nie ma już w repertuarze.", " no longer in the repertoire.")
            : () => T($"Otwarto profil: {name}", $"Profile opened: {name}"));
    }

    /// <summary>An opened profile has only past screenings: offer to remove its scheduled task.</summary>
    private async Task RetireExpiredProfileAsync(string path)
    {
        SetError(() => T("Wszystkie seanse z tego profilu już się odbyły.", "All screenings of this profile are over."));
        var taskName = SchtasksScheduler.TaskNameFor(path);
        if (!await _scheduler.ExistsAsync(taskName)) return;

        if (!_ui.Confirm(T("Profil nieaktualny", "Outdated profile"),
                T($"Wszystkie seanse z tego profilu już się odbyły, a w Harmonogramie nadal jest zadanie {taskName}.\n\nUsunąć je?",
                  $"All screenings of this profile are over, but Task Scheduler still has the task {taskName}.\n\nRemove it?")))
            return;

        var result = await _scheduler.DeleteAsync(taskName);
        var message = result.Message;
        if (result.Success)
        {
            SetError(null);
            SetStatus(() => message + T(" Profil był nieaktualny.", " The profile was outdated."));
        }
        else
        {
            SetError(() => message);
        }
    }

    // ------------------------------------------------------------------ navigation

    private async Task GoNextAsync()
    {
        SetError(null);
        try
        {
            switch (_stepIndex)
            {
                case DatesIndex:
                    await LeaveDatesStepAsync();
                    break;
                case ScreeningsIndex:
                    await LeaveScreeningsStepAsync();
                    break;
                case SeatsIndex:
                    LeaveSeatsStep();
                    break;
            }
        }
        catch (OperationCanceledException) when (_loadCts?.IsCancellationRequested == true)
        {
            // User went back while loading.
        }
        catch (Exception ex)
        {
            SetError(() => ErrorMessages.Describe(ex));
            SetStatus(null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> LeaveDatesStepAsync()
    {
        var today = _clock();
        if (DatesStep.Validate(today) is not null)
        {
            // Re-evaluated on language change, and disappears once the input is fixed.
            SetError(() => DatesStep.Validate(today));
            return false;
        }

        var dates = DatesStep.GetDates();
        IsBusy = true;
        SetStatus(() => T("Pobieranie repertuaru (", "Loading the repertoire (")
                        + Count(dates.Count, "dzień", "dni", "dni", "day", "days") + ")…");
        var cancellationToken = StartLoad();

        var results = await LoadRepertoireAsync(DatesStep.Cinema.Id, dates, cancellationToken);
        var failures = results.Where(r => r.Error is not null).ToList();
        if (failures.Count == results.Count)
            throw failures[0].Error!;

        ScreeningsStep.Load(
            results.Where(r => r.Day is not null).Select(r => r.Day!).ToList(),
            ScreeningFilter.ImaxWithoutDubbing,
            _clock());

        SetStatus(failures.Count == 0
            ? null
            : () => T("Nie udało się pobrać repertuaru dla ", "Could not load the repertoire for ")
                    + Count(failures.Count, "dnia", "dni", "dni", "day", "days") + ": "
                    + string.Join(", ", failures.Select(f => Formatting.ShortDate(f.Date))));
        GoTo(ScreeningsIndex);
        return true;
    }

    private async Task<bool> LeaveScreeningsStepAsync()
    {
        var selected = ScreeningsStep.GetSelectedScreenings();
        if (selected.Count == 0)
        {
            SetError(() => T("Zaznacz co najmniej jeden seans.", "Select at least one screening."));
            return false;
        }

        GoTo(SeatsIndex);
        IsBusy = true;
        SetStatus(() => T("Pobieranie układu sali i zajętości miejsc…", "Loading the hall layout and seat occupancy…"));
        await SeatsStep.LoadAsync(_seatService, selected, StartLoad());
        SetStatus(() => SeatsStep.Summary);
        return true;
    }

    private bool LeaveSeatsStep()
    {
        if (SeatsStep.Validate() is not null)
        {
            SetError(() => SeatsStep.Validate());
            return false;
        }

        SummaryStep.Load(SeatsStep.Items, SeatsStep.MinAdjacent, DefaultProfileName());
        SetStatus(null);
        GoTo(SummaryIndex);
        return true;
    }

    private void GoBack()
    {
        if (!CanGoBack) return;
        _loadCts?.Cancel();
        SetError(null);
        SetStatus(null);
        GoTo(_stepIndex - 1);
    }

    private void GoTo(int index)
    {
        _stepIndex = index;
        CurrentPage = index switch
        {
            DatesIndex => DatesStep,
            ScreeningsIndex => ScreeningsStep,
            SeatsIndex => SeatsStep,
            _ => SummaryStep,
        };
        UpdateSteps();
        OnPropertyChanged(nameof(StepIndex));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastStep));
        RefreshCommands();
    }

    private CancellationToken StartLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    private async Task<IReadOnlyList<RepertoireResult>> LoadRepertoireAsync(
        string cinemaId, IReadOnlyList<DateOnly> dates, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxParallelRequests);
        var tasks = dates.Select(async date =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return new RepertoireResult(date, await _client.GetRepertoireAsync(cinemaId, date, cancellationToken), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new RepertoireResult(date, null, ex);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private void UpdateSteps()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsCurrent = i == _stepIndex;
            Steps[i].IsDone = i < _stepIndex;
        }
    }

    private void RefreshCommands()
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        NewProfileCommand.NotifyCanExecuteChanged();
        OpenProfileCommand.NotifyCanExecuteChanged();
    }

    private sealed record RepertoireResult(DateOnly Date, DayRepertoire? Day, Exception? Error);
}
