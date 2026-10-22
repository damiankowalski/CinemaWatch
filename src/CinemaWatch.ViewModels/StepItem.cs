using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>
/// One entry of the wizard step indicator. Steps are clickable: any step already visited (and the next one)
/// can be opened directly; going forward runs the same checks and loading as "Next".
/// </summary>
public sealed class StepItem : ObservableObject
{
    private readonly string _polishTitle;
    private readonly string _englishTitle;
    private bool _isCurrent;
    private bool _isDone;
    private bool _isReachable;

    public StepItem(int number, string polishTitle, string englishTitle, Func<int, Task> navigate)
    {
        Number = number;
        _polishTitle = polishTitle;
        _englishTitle = englishTitle;
        NavigateCommand = new AsyncRelayCommand(() => navigate(Index), () => IsReachable);
    }

    public int Number { get; }
    public int Index => Number - 1;
    public string Title => T(_polishTitle, _englishTitle);
    public AsyncRelayCommand NavigateCommand { get; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
                OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>Before the current step (shown with a check mark).</summary>
    public bool IsDone
    {
        get => _isDone;
        set => SetProperty(ref _isDone, value);
    }

    /// <summary>Can be opened by clicking (visited before, or the next step).</summary>
    public bool IsReachable
    {
        get => _isReachable;
        set
        {
            if (!SetProperty(ref _isReachable, value)) return;
            OnPropertyChanged(nameof(ToolTip));
            NavigateCommand.NotifyCanExecuteChanged();
        }
    }

    public string? ToolTip => IsCurrent
        ? null
        : IsReachable
            ? T("Przejdź do tego kroku", "Go to this step")
            : T("Najpierw przejdź poprzednie kroki", "Complete the previous steps first");

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTip));
    }
}
