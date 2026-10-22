using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>One entry of the wizard step indicator.</summary>
public sealed class StepItem(int number, string polishTitle, string englishTitle) : ObservableObject
{
    private bool _isCurrent;
    private bool _isDone;

    public int Number { get; } = number;
    public string Title => T(polishTitle, englishTitle);

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => SetProperty(ref _isDone, value);
    }

    public void RefreshTexts() => OnPropertyChanged(nameof(Title));
}
