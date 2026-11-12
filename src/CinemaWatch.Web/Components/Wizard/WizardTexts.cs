using CinemaWatch.Core;
using CinemaWatch.Server.Polling;
using CinemaWatch.Server.Wizard;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Web.Components.Wizard;

/// <summary>Texts and addresses of the site's wizard (docs/SAAS.md §5.2), shared by its pages and its form endpoint.</summary>
public static class WizardTexts
{
    /// <summary>The desktop app's step titles.</summary>
    public static string StepTitle(int step) => step switch
    {
        1 => T("Kino i termin", "Cinema and dates"),
        2 => T("Film i seanse", "Film and screenings"),
        3 => T("Miejsca", "Seats"),
        _ => T("Podsumowanie", "Summary"),
    };

    /// <summary>The wizard's heading: "Nowy profil", or "Edycja profilu" when a stored profile is edited.</summary>
    public static string Heading(WizardState state) => state.ProfileId is null
        ? T("Nowy profil", "New profile")
        : T("Edycja profilu", "Editing a profile");

    /// <summary>"/wizard?…" (step 1) or "/wizard/2?…".</summary>
    public static string StepUrl(int step, WizardState state) =>
        (step <= 1 ? "/wizard" : $"/wizard/{step}") + "?" + state.Query();

    /// <summary>A loading's progress page; <paramref name="to"/> = 4 when it was started from step 4 (and leads back there).</summary>
    public static string LoadingUrl(Guid id, WizardState state, int? to = null) =>
        $"/wizard/loading/{id}?{state.Query()}" + (to is { } step ? $"&to={step}" : "");

    /// <summary>
    /// Where a finished loading leads: editing with seats still waiting for their hall maps goes through /wizard/seed
    /// (which fills them in); otherwise the step itself.
    /// </summary>
    public static string AfterLoadingUrl(int step, WizardState state) =>
        state.ProfileId is not null && state.SeedIds.Count > 0 && step >= 3
            ? $"/wizard/seed?{state.Query()}&to={step}"
            : StepUrl(step, state);

    /// <summary>Why a loading did not start or stopped, and when to try again.</summary>
    public static string StopReason(WizardStopReason reason, DateTimeOffset? retryAt)
    {
        var at = retryAt is { } when ? PollerTime.ClockText(when) : null;
        return reason switch
        {
            WizardStopReason.PausedByOperator => T("Pobieranie danych z Cinema City jest wstrzymane przez właściciela serwisu. Spróbuj później.",
                                                   "Loading data from Cinema City is paused by the site's owner. Try again later."),
            WizardStopReason.Blocked => at is null
                ? T("Cinema City odrzuciło zapytania serwera. Spróbuj później.", "Cinema City refused the server's requests. Try again later.")
                : T($"Cinema City odrzuciło zapytania serwera. Spróbuj ponownie po {at}.", $"Cinema City refused the server's requests. Try again after {at}."),
            WizardStopReason.BudgetUsedUp => at is null
                ? T("Kreator wykorzystał na razie wspólny limit zapytań do Cinema City. Spróbuj później.", "The wizard has used up the shared limit of requests to Cinema City for now. Try again later.")
                : T($"Kreator wykorzystał na razie wspólny limit zapytań do Cinema City. Spróbuj ponownie po {at}.", $"The wizard has used up the shared limit of requests to Cinema City for now. Try again after {at}."),
            WizardStopReason.AccountLimit => at is null
                ? T("Twój limit zapytań do Cinema City na tę godzinę jest wykorzystany. Spróbuj później.", "Your limit of requests to Cinema City for this hour is used up. Try again later.")
                : T($"Twój limit zapytań do Cinema City na tę godzinę jest wykorzystany. Spróbuj ponownie po {at}.", $"Your limit of requests to Cinema City for this hour is used up. Try again after {at}."),
            WizardStopReason.TooLarge => T("To wyszukiwanie potrzebuje więcej zapytań, niż pozwalają obecne limity. Wybierz krótszy zakres dat albo jedno miasto.",
                                           "This search needs more requests than the current limits allow. Choose a shorter date range or one city."),
            _ => T("Serwer był restartowany. Spróbuj ponownie.", "The server was restarted. Try again."),
        };
    }

    /// <summary>A request the server refused although the page's own checks passed (e.g. a changed form).</summary>
    public static string Problem(WizardRequestProblem problem) => problem switch
    {
        WizardRequestProblem.NoCinema or WizardRequestProblem.UnknownCinema => T("Wybierz miasto.", "Choose a city."),
        WizardRequestProblem.DateInPast => T("Data nie może być z przeszłości.", "The date cannot be in the past."),
        WizardRequestProblem.DatesReversed => T("Data końcowa nie może być wcześniejsza niż początkowa.", "The end date cannot be before the start date."),
        WizardRequestProblem.RangeTooLong => T("Zakres dat jest za długi.", "The date range is too long."),
        _ => T("Wybór jest niepoprawny. Zacznij od nowa.", "The choice is not valid. Start again."),
    };

    /// <summary>"Warszawa 12.11, Kraków 13.11" (city only when several cinemas are searched).</summary>
    public static string Items(IEnumerable<WizardItem> items, bool withCity) => string.Join(", ", items.Select(item =>
        (withCity ? Locations.FindCinema(item.CinemaId)?.DisplayCity + " " : "") + (item.Date is { } date ? ScreeningText.ShortDate(date) : "")));

    /// <summary>"Warszawa, 12.11" or "Wszystkie miasta, 12.11–25.11".</summary>
    public static string Search(WizardState state)
    {
        var place = state.IsAllCities ? T("Wszystkie miasta", "All cities") : Locations.FindCinema(state.City)?.DisplayCity ?? "";
        var dates = state.Dates;
        var when = dates.Count switch
        {
            0 => "",
            1 => ScreeningText.ShortDate(dates[0]),
            _ => $"{ScreeningText.ShortDate(dates[0])}–{ScreeningText.ShortDate(dates[^1])}",
        };
        return $"{place}, {when}";
    }
}
