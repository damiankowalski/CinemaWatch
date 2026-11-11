using CinemaWatch.Core;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using CinemaWatch.Server.Wizard;
using CinemaWatch.Web.Components.Account;
using CinemaWatch.Web.Components.Wizard;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using static CinemaWatch.Core.Localizer;

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// The wizard's one form endpoint (docs/SAAS.md §5.2). Every page of the wizard posts its whole form here with the
/// button that was pressed ("go"): a step number from the step bar or the Back/Next buttons, or an action of step 2.
/// Backwards is immediate; forwards runs the same checks and loading as the desktop app's "Next" and stops at the
/// first step that needs the user. The answer is always a redirect to a page that carries the state in its address,
/// so a reload never posts again.
/// </summary>
internal static class WizardEndpoints
{
    public static void MapWizardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/wizard/go", async (HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users,
            WizardJobs jobs, CinemaCityCache cache, PollerSettings settings, TimeProvider time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            var userId = users.GetUserId(context.User);
            if (userId is null)
                return Results.NotFound();

            var form = await context.Request.ReadFormAsync();
            var state = WizardState.Read(name => form[name]);
            var step = form["step"] == "2" ? 2 : 1;
            var go = form["go"].ToString();
            var now = time.GetUtcNow();
            var today = CinemaCityCache.PolishToday(now);

            if (step == 2)
                state = ApplyChoices(state, go, cache, PolishTime.FromUtc(now.UtcDateTime));

            switch (go)
            {
                case "1":
                    return Results.LocalRedirect(WizardTexts.StepUrl(1, state));
                case "film" or "all" or "none":
                    return Results.LocalRedirect(WizardTexts.StepUrl(2, state));
                case "2" when step == 2:
                    return Results.LocalRedirect(WizardTexts.StepUrl(2, state));
                case "2" or "reload":
                    return await LoadRepertoireAsync(context, jobs, cache, userId, state, today);
                case "3" or "4" when step == 1:
                    // Like pressing "Next" repeatedly: step 2 needs the user's choice, so the way stops there.
                    return await LoadRepertoireAsync(context, jobs, cache, userId, state, today);
                case "3" or "4":
                    if (state.SelectionProblem(settings.Limits.MaxScreeningsPerProfile) is { } problem)
                        return Refuse(context, 2, state, problem);
                    // Step 3 (the hall map) follows in stage 6.4.4.
                    IdentityRedirectManager.SetStatus(context, StatusMessageKind.Success,
                        T("Wybór seansów jest w porządku. Krok 3 (mapa sali) jest jeszcze w budowie.", "The selection is fine. Step 3 (the hall map) is still being built."));
                    return Results.LocalRedirect(WizardTexts.StepUrl(2, state));
                default:
                    return Results.LocalRedirect(WizardTexts.StepUrl(step, state));
            }
        });
    }

    /// <summary>
    /// Step 2's form: the film from the list, the ticked screenings - only those of that film that are listed (a film
    /// change clears the ticks, as in the desktop app) - or all/none of them.
    /// </summary>
    private static WizardState ApplyChoices(WizardState state, string go, CinemaCityCache cache, DateTime polishNow)
    {
        if (state.Dates.Count == 0)
            return state;
        var repertoire = CachedRepertoire.Read(cache, state.Repertoire());
        if (repertoire.Days.Count == 0)
            return state; // nothing to compare with (expired): keep what came
        var choices = ScreeningChoices.Build(repertoire.Days, ScreeningFilter.ImaxWithoutDubbing, polishNow, state.FilmId);
        var listed = choices.Screenings.Select(s => s.Id).ToList();
        var ids = go switch
        {
            "all" => listed,
            "none" => [],
            _ => state.ScreeningIds.Where(listed.Contains).ToList(),
        };
        return state with { FilmId = choices.Film?.FilmId, ScreeningIds = ids };
    }

    /// <summary>
    /// Step 1 → 2: the dates are checked as in the desktop app; a repertoire the shared cache holds completely opens
    /// step 2 at once; otherwise a loading starts in the background and its progress page opens.
    /// </summary>
    private static async Task<IResult> LoadRepertoireAsync(HttpContext context, WizardJobs jobs, CinemaCityCache cache, string userId, WizardState state, DateOnly today)
    {
        if (state.DatesProblem(today) is { } problem)
            return Refuse(context, 1, state, problem);

        var request = state.Repertoire();
        var next = state with { Furthest = Math.Max(state.Furthest, 2) };
        if (CachedRepertoire.Read(cache, request).Complete)
            return Results.LocalRedirect(WizardTexts.StepUrl(2, next));

        var start = await jobs.StartAsync(userId, request, context.RequestAborted);
        switch (start.Outcome)
        {
            case WizardStartOutcome.Started:
                return Results.LocalRedirect(WizardTexts.LoadingUrl(start.Job!.Id, next));
            case WizardStartOutcome.AlreadyRunning:
                IdentityRedirectManager.SetStatus(context, StatusMessageKind.Error,
                    T("Twoje poprzednie ładowanie jeszcze trwa. Gdy się skończy, możesz spróbować ponownie.",
                      "Your previous loading is still running. When it is done, you can try again."));
                return Results.LocalRedirect(WizardTexts.LoadingUrl(start.Job!.Id, state));
            case WizardStartOutcome.Invalid:
                return Refuse(context, 1, state, WizardTexts.Problem(start.Problem!.Value));
            default:
                return Refuse(context, 1, state, WizardTexts.StopReason(start.Reason!.Value, start.RetryAt));
        }
    }

    private static IResult Refuse(HttpContext context, int step, WizardState state, string message)
    {
        IdentityRedirectManager.SetStatus(context, StatusMessageKind.Error, message);
        return Results.LocalRedirect(WizardTexts.StepUrl(step, state));
    }
}
