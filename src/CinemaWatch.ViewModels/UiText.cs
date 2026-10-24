using CinemaWatch.Core;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.ViewModels;

/// <summary>
/// Fixed UI texts for the views, bound as <c>{Binding X, Source={StaticResource T}}</c>. When the language
/// changes, every binding to this object refreshes at once.
/// </summary>
public sealed class UiText : ObservableObject
{
    private UiText()
    {
        Localizer.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public static UiText Instance { get; } = new();

    // Window
    public string AppSubtitle => T("Monitor wolnych miejsc · Cinema City", "Seat availability monitor · Cinema City");
    public string NewProfile => T("Nowy profil", "New profile");
    public string OpenProfile => T("Otwórz profil…", "Open profile…");
    public string LanguageToolTip => T("Język aplikacji", "App language");
    public string Back => T("Wstecz", "Back");
    public string Next => T("Dalej", "Next");

    // Step 1
    public string DatesTitle => T("Kino i termin", "Cinema and dates");
    public string DatesIntro => T("Wybierz dzień albo zakres dni, w których chcesz szukać seansów.",
        "Choose a day or a range of days to search for screenings.");
    public string Cinema => T("Kino", "Cinema");
    public string City => T("Miasto", "City");
    public string Technology => T("Technologia", "Technology");
    public string LocationHint => T("W każdym mieście jest jedna sala IMAX Cinema City, więc kino wynika z miasta. Technologia jest na razie stała.",
        "Each city has one Cinema City IMAX hall, so the cinema follows from the city. The technology is fixed for now.");
    public string Dates => T("Termin", "Dates");
    public string DateRange => T("Zakres dat", "Date range");
    public string DateTo => T("Do", "To");

    // Step 2
    public string ScreeningsTitle => T("Film i seanse", "Film and screenings");
    public string ScreeningsIntro => T("Widoczne są seanse IMAX bez dubbingu. Wyprzedane też możesz zaznaczyć - właśnie je warto monitorować.",
        "IMAX screenings without dubbing are shown. You can tick sold-out ones too - those are the ones worth watching.");
    public string Film => T("Film", "Film");
    public string Screenings => T("Seanse", "Screenings");
    public string SelectAll => T("Zaznacz wszystkie", "Select all");
    public string Clear => T("Wyczyść", "Clear");

    // Step 3
    public string Screen => T("EKRAN", "SCREEN");
    public string SeatsTogether => T("Miejsc obok siebie", "Seats together");
    public string SeatsTogetherToolTip => T("Powiadamiaj tylko, gdy tyle zaznaczonych miejsc będzie wolnych obok siebie",
        "Notify only when this many watched seats are free next to each other");
    public string ApplyToAll => T("Zastosuj do wszystkich seansów", "Apply to all screenings");
    public string LegendFree => T("Wolne", "Free");
    public string LegendTaken => T("Zajęte", "Taken");
    public string LegendWatched => T("Monitorowane", "Watched");
    public string LegendWheelchair => T("Dla osoby na wózku", "Wheelchair space");

    // Step 4
    public string SummaryTitle => T("Podsumowanie", "Summary");
    public string BuyTicket => T("Kup bilet", "Buy ticket");
    public string ExpandToolTip => T("Pokaż lub ukryj wybrane miejsca", "Show or hide the selected seats");
    public string Profile => T("Profil", "Profile");
    public string Name => T("Nazwa", "Name");
    public string File => T("Plik", "File");
    public string CheckNow => T("Sprawdź ten profil", "Check this profile");
    public string SaveProfile => T("Zapisz profil…", "Save profile…");
    public string CopyCommand => T("Kopiuj polecenie", "Copy command");
    public string CopyCommandToolTip => T("Kopiuje do schowka polecenie uruchamiające sprawdzenie tego profilu bez okna",
        "Copies the command that checks this profile without a window");
    public string TaskScheduler => T("Harmonogram zadań", "Task Scheduler");
    public string CheckEvery => T("Sprawdzaj co", "Check every");
    public string AddToScheduler => T("Dodaj do Harmonogramu", "Add to Task Scheduler");
    public string RemoveFromScheduler => T("Usuń z Harmonogramu", "Remove from Task Scheduler");

    // Dialogs
    public string ProfileFileFilter => T(
        "Profil CinemaWatch (*.cinemawatch.json)|*.cinemawatch.json|Wszystkie pliki (*.*)|*.*",
        "CinemaWatch profile (*.cinemawatch.json)|*.cinemawatch.json|All files (*.*)|*.*");
    public string OpenProfileDialogTitle => T("Otwórz profil CinemaWatch", "Open CinemaWatch profile");
    public string SaveProfileDialogTitle => T("Zapisz profil CinemaWatch", "Save CinemaWatch profile");
    public string UnexpectedError => T("CinemaWatch - nieoczekiwany błąd", "CinemaWatch - unexpected error");
    public string UnsavedOnExitTitle => T("Niezapisane zmiany", "Unsaved changes");
    public string UnsavedOnExit => T("Niektóre profile mają niezapisane zmiany. Zamknąć aplikację mimo to?",
        "Some profiles have unsaved changes. Close the app anyway?");
}
