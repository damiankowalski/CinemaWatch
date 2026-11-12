using CinemaWatch.Core;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using CinemaWatch.Server.Profiles;
using CinemaWatch.Server.Wizard;
using CinemaWatch.Web.Components.Account;
using CinemaWatch.Web.Components.Wizard;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using static CinemaWatch.Core.Localizer;

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// The wizard's one form endpoint (docs/SAAS.md §5.2). Every page of the wizard posts its whole form here with the
/// button that was pressed ("go"): a step number from the step bar or the Back/Next buttons, or an action of a step.
/// Backwards is immediate; forwards runs the same checks and loading as the desktop app's "Next" and stops at the
/// first step that needs the user. The answer is always a redirect to a page that carries the state in its address,
/// so a reload never posts again.
/// </summary>
internal static class WizardEndpoints
{
    public static void MapWizardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/wizard/go", async (HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users,
            WizardJobs jobs, CinemaCityCache cache, PollerSettings settings, ProfileService profiles, TimeProvider time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            var userId = users.GetUserId(context.User);
            if (userId is null)
                return Results.NotFound();

            var form = await context.Request.ReadFormAsync();
            var state = WizardState.Read(name => form[name]);
            var step = form["step"].ToString() switch { "2" => 2, "3" => 3, "4" => 4, _ => 1 };
            var go = form["go"].ToString();
            var now = time.GetUtcNow();
            var today = CinemaCityCache.PolishToday(now);
            var maxScreenings = settings.Limits.MaxScreeningsPerProfile;

            if (step == 2)
                state = ApplyChoices(state, go, cache, PolishTime.FromUtc(now.UtcDateTime));
            IReadOnlyList<ScreeningResult> maps = [];
            if (step == 3)
            {
                maps = await jobs.PeekScreeningsAsync(state.ScreeningIds, context.RequestAborted);
                state = ApplySeats(state, form, maps);
            }

            switch (go)
            {
                case "1":
                    return Results.LocalRedirect(WizardTexts.StepUrl(1, state));
                case "film" or "all" or "none":
                    return Results.LocalRedirect(WizardTexts.StepUrl(2, state));
                case "2" when step >= 2:
                    return Results.LocalRedirect(WizardTexts.StepUrl(2, state));
                case "2" or "reload" when step <= 2:
                case "3" or "4" when step == 1:
                    // From step 1 the way stops at step 2: it needs the user's choice.
                    return await LoadRepertoireAsync(context, jobs, cache, userId, state, today);
                case "3" or "4" when step == 2:
                    if (state.SelectionProblem(maxScreenings) is { } problem)
                        return Refuse(context, 2, state, problem);
                    return await LoadMapsAsync(context, jobs, userId, state, 2, onlyWhenMissing: true);
                case "reload" when step == 4:
                    // Editing: the hall maps the stored profile's seats still wait for (else all of them).
                    return await StartAsync(context, jobs, userId,
                        new ScreeningsRequest(state.SeedIds.Count > 0 ? state.SeedIds : state.ScreeningIds), state, state, 4);
                case "3" when step == 4:
                    return Results.LocalRedirect(WizardTexts.StepUrl(3, state));
                case "save" when step == 4:
                    return await SaveAsync(context, jobs, cache, profiles, settings, userId, state);
                case "reload":
                    return await LoadMapsAsync(context, jobs, userId, state, 3, onlyWhenMissing: false);
                case "copy":
                    return CopyToSameLayout(context, state, maps);
                case "clear" when state.CurrentTab is { } tab:
                    return Results.LocalRedirect(WizardTexts.StepUrl(3, state.WithSeats(tab, "")));
                case "4":
                    if (state.Seats.Count == 0 && state.SeedIds.Count == 0)
                        return Refuse(context, 3, state, T("Zaznacz co najmniej jeden rząd albo jedno miejsce.", "Select at least one row or seat."));
                    return Results.LocalRedirect(WizardTexts.StepUrl(4, state with { Furthest = WizardState.LastStep }));
                default:
                    if (step == 3 && go.StartsWith("tab:", StringComparison.Ordinal) && state.ScreeningIds.Contains(go[4..]))
                        return Results.LocalRedirect(WizardTexts.StepUrl(3, state with { Tab = go[4..] }));
                    if (step == 3 && go.StartsWith("row:", StringComparison.Ordinal))
                        return Results.LocalRedirect(WizardTexts.StepUrl(3, ToggleRow(state, go[4..], maps)));
                    return Results.LocalRedirect(WizardTexts.StepUrl(step, state));
            }
        });

        // "Edit" on the profile list: the wizard opened on the stored profile, as the desktop app replays one.
        endpoints.MapPost("/wizard/edit/{id:guid}", async (Guid id, HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users,
            WizardJobs jobs, ProfileService profiles, TimeProvider time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            var userId = users.GetUserId(context.User);
            if (userId is null || await profiles.OpenAsync(userId, id) is not { } profile)
                return Results.NotFound();

            var state = WizardProfile.FromProfile(id, profile.Name, profile.Document, PolishTime.FromUtc(time.GetUtcNow().UtcDateTime), out var dropped);
            if (state.ScreeningIds.Count == 0)
            {
                IdentityRedirectManager.SetStatus(context, StatusMessageKind.Error,
                    T($"Wszystkie seanse profilu „{profile.Name}” już się zaczęły: nie ma czego edytować.", $"All screenings of the profile \"{profile.Name}\" have started: there is nothing to edit."));
                return Results.LocalRedirect($"/?profile={id}#profile-{id}");
            }
            state = WizardProfile.Seed(state, profile.Document, await jobs.PeekScreeningsAsync(state.SeedIds, context.RequestAborted));
            if (dropped > 0)
                IdentityRedirectManager.SetStatus(context, StatusMessageKind.Success,
                    T("Pominięto ", "Skipped ") + Count(dropped, "seans", "seanse", "seansów", "screening", "screenings")
                    + T(", które już się zaczęły.", " that have already started."));
            if (state.SeedIds.Count == 0)
                return Results.LocalRedirect(WizardTexts.StepUrl(4, state));
            // Some hall maps are not known (never loaded, or long ago): load them; the loading ends at /wizard/seed.
            return await StartAsync(context, jobs, userId, new ScreeningsRequest(state.SeedIds), state, state, 4);
        });

        // After a loading that brought hall maps: the stored profile's seats for them (no requests, nothing stored).
        endpoints.MapGet("/wizard/seed", async (HttpContext context, UserManager<AppUser> users, WizardJobs jobs, ProfileService profiles) =>
        {
            var userId = users.GetUserId(context.User);
            if (userId is null)
                return Results.NotFound();
            var query = context.Request.Query;
            var state = WizardState.Read(name => query[name]);
            var to = query["to"].ToString() == "3" ? 3 : 4;
            if (state.ProfileId is not { } id || state.SeedIds.Count == 0)
                return Results.LocalRedirect(WizardTexts.StepUrl(to, state));
            if (await profiles.OpenAsync(userId, id) is not { } profile)
            {
                IdentityRedirectManager.SetStatus(context, StatusMessageKind.Error, ProfileGone);
                return Results.LocalRedirect("/");
            }
            state = WizardProfile.Seed(state, profile.Document, await jobs.PeekScreeningsAsync(state.SeedIds, context.RequestAborted));
            return Results.LocalRedirect(WizardTexts.StepUrl(to, state));
        });
    }

    private static string ProfileGone => T("Tego profilu już nie ma (został usunięty).", "This profile is gone (it was deleted).");

    /// <summary>
    /// Step 4's "Save": the profile built as the desktop app builds it, then stored through the import's checks and
    /// limits. A saved profile opens on the profile list; a problem goes back to step 4 with its message.
    /// </summary>
    private static async Task<IResult> SaveAsync(HttpContext context, WizardJobs jobs, CinemaCityCache cache, ProfileService profiles,
        PollerSettings settings, string userId, WizardState state)
    {
        WatchProfile? edited = null;
        if (state.ProfileId is { } editing)
        {
            edited = (await profiles.OpenAsync(userId, editing, context.RequestAborted))?.Document;
            if (edited is null)
            {
                IdentityRedirectManager.SetStatus(context, StatusMessageKind.Error, ProfileGone);
                return Results.LocalRedirect("/");
            }
        }
        var maps = await jobs.PeekScreeningsAsync(state.ScreeningIds, context.RequestAborted);
        var days = state.Dates.Count > 0 ? CachedRepertoire.Read(cache, state.Repertoire()).Days : [];
        var draft = WizardProfile.Build(state, maps, days, edited, settings.Limits.MinIntervalMinutes);
        if (draft.Profile is not { } profile)
            return Refuse(context, 4, state, draft.Problem!);

        var result = await profiles.SaveAsync(userId, profile, state.ProfileId, context.RequestAborted);
        var name = result.Name;
        switch (result.Outcome)
        {
            case SaveOutcome.Added or SaveOutcome.Updated:
                IdentityRedirectManager.SetStatus(context, StatusMessageKind.Success, result.Outcome == SaveOutcome.Added
                    ? T($"Zapisano profil „{name}”. Serwer zacznie go sprawdzać w ciągu minuty.", $"Saved the profile \"{name}\". The server starts checking it within a minute.")
                    : T($"Zapisano zmiany w profilu „{name}”.", $"Saved the changes to the profile \"{name}\"."));
                return Results.LocalRedirect($"/?profile={result.Id}#profile-{result.Id}");
            case SaveOutcome.NameTaken:
                return Refuse(context, 4, state, T($"Masz już profil o nazwie „{name}”. Wpisz inną nazwę.", $"You already have a profile named \"{name}\". Type another name."));
            case SaveOutcome.LimitReached:
                return Refuse(context, 4, state, T("Masz już tyle profili, ile pozwala limit konta. Usuń któryś, żeby zapisać nowy.",
                                                   "You already have as many profiles as your account's limit allows. Delete one to save a new one."));
            case SaveOutcome.NotFound:
                IdentityRedirectManager.SetStatus(context, StatusMessageKind.Error, ProfileGone);
                return Results.LocalRedirect("/");
            default:
                return Refuse(context, 4, state, T("Profilu nie można zapisać: ", "The profile cannot be saved: ") + string.Join(" ", result.Errors));
        }
    }

    /// <summary>
    /// Step 2's form: the film from the list, the ticked screenings - only those of that film that are listed (a film
    /// change clears the ticks, as in the desktop app) - or all/none of them. Seats of screenings no longer ticked go.
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
        return state with
        {
            FilmId = choices.Film?.FilmId,
            ScreeningIds = ids,
            Seats = state.Seats.Where(s => ids.Contains(s.Key)).ToDictionary(StringComparer.Ordinal),
            Tab = state.Tab is { } tab && ids.Contains(tab) ? tab : null,
        };
    }

    /// <summary>
    /// Step 3's form: the shown map's check boxes ("seat", only keys of its layout) replace that screening's
    /// selection - only when the form really showed that map ("shown"), so a map that could not be drawn keeps its seats.
    /// </summary>
    private static WizardState ApplySeats(WizardState state, IFormCollection form, IReadOnlyList<ScreeningResult> maps)
    {
        var shown = form["shown"].ToString();
        if (shown.Length == 0 || shown != state.CurrentTab || maps.FirstOrDefault(m => m.ScreeningId == shown)?.Plan is not { } plan)
            return state;
        var keys = form["seat"].Where(k => k is not null).Select(k => k!).ToHashSet(StringComparer.Ordinal);
        return state.WithSeats(shown, SeatCode.Encode(plan, keys));
    }

    /// <summary>A row number without JavaScript: all regular seats of the row on, or off when all were on (Core rules).</summary>
    private static WizardState ToggleRow(WizardState state, string row, IReadOnlyList<ScreeningResult> maps)
    {
        var separator = row.IndexOf('|');
        if (separator <= 0 || state.CurrentTab is not { } tab || maps.FirstOrDefault(m => m.ScreeningId == tab)?.Plan is not { } plan)
            return state;
        var (section, rowKey) = (row[..separator], row[(separator + 1)..]);
        if (!plan.Seats.Any(s => s.Section == section && s.RowKey == rowKey))
            return state;
        var before = SeatCode.Decode(plan, state.Seats.GetValueOrDefault(tab));
        return state.WithSeats(tab, SeatCode.Encode(plan, SeatSelection.ToggleRow(before, plan, section, rowKey)));
    }

    /// <summary>The desktop's "Apply to all": the shown selection goes to every other screening in the same hall layout.</summary>
    private static IResult CopyToSameLayout(HttpContext context, WizardState state, IReadOnlyList<ScreeningResult> maps)
    {
        if (state.CurrentTab is not { } tab || maps.FirstOrDefault(m => m.ScreeningId == tab) is not { Details: { } source, Plan: not null })
            return Results.LocalRedirect(WizardTexts.StepUrl(3, state));
        var code = state.Seats.GetValueOrDefault(tab) ?? "";
        var copied = 0;
        var skipped = 0;
        foreach (var map in maps.Where(m => m.ScreeningId != tab))
        {
            if (map.Details is { } details && map.Plan is not null && (details.VenueId, details.SeatplanId) == (source.VenueId, source.SeatplanId))
            {
                state = state.WithSeats(map.ScreeningId, code);
                copied++;
            }
            else
                skipped++;
        }
        var message = copied == 0
            ? T("Brak innych seansów z tą samą salą.", "No other screenings in the same hall.")
            : T($"Skopiowano zaznaczenie do {copied} {Plural(copied, "seansu", "seansów", "seansów", "", "")}",
                $"Selection copied to {Count(copied, "", "", "", "screening", "screenings")}")
              + (skipped > 0
                  ? T($" (pominięto {skipped}: inna sala lub błąd pobierania).", $" ({skipped} skipped: different hall or loading error).")
                  : ".");
        IdentityRedirectManager.SetStatus(context, StatusMessageKind.Success, message);
        return Results.LocalRedirect(WizardTexts.StepUrl(3, state));
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
        return await StartAsync(context, jobs, userId, request, state, next, 1);
    }

    /// <summary>
    /// Step 2 → 3 (or "Refresh occupancy" on step 3): the hall maps' data. When everything is known (details, layout,
    /// free seats young enough) and only missing data is wanted, step 3 opens at once; otherwise a loading starts.
    /// </summary>
    private static async Task<IResult> LoadMapsAsync(HttpContext context, WizardJobs jobs, string userId, WizardState state, int from, bool onlyWhenMissing)
    {
        if (state.ScreeningIds.Count == 0)
            return Results.LocalRedirect(WizardTexts.StepUrl(2, state));
        var next = state with { Furthest = Math.Max(state.Furthest, 3) };
        if (onlyWhenMissing)
        {
            var maps = await jobs.PeekScreeningsAsync(state.ScreeningIds, context.RequestAborted);
            if (maps.All(m => m.Error is null && m.FreeSeatKeys is not null))
                return Results.LocalRedirect(WizardTexts.StepUrl(3, next));
        }
        return await StartAsync(context, jobs, userId, new ScreeningsRequest(state.ScreeningIds), state, next, from);
    }

    private static async Task<IResult> StartAsync(HttpContext context, WizardJobs jobs, string userId, WizardRequest request, WizardState state, WizardState next, int from)
    {
        var start = await jobs.StartAsync(userId, request, context.RequestAborted);
        switch (start.Outcome)
        {
            case WizardStartOutcome.Started:
                return Results.LocalRedirect(WizardTexts.LoadingUrl(start.Job!.Id, next, from == 4 ? 4 : null));
            case WizardStartOutcome.AlreadyRunning:
                IdentityRedirectManager.SetStatus(context, StatusMessageKind.Error,
                    T("Twoje poprzednie ładowanie jeszcze trwa. Gdy się skończy, możesz spróbować ponownie.",
                      "Your previous loading is still running. When it is done, you can try again."));
                return Results.LocalRedirect(WizardTexts.LoadingUrl(start.Job!.Id, state));
            case WizardStartOutcome.Invalid:
                return Refuse(context, from, state, WizardTexts.Problem(start.Problem!.Value));
            default:
                return Refuse(context, from, state, WizardTexts.StopReason(start.Reason!.Value, start.RetryAt));
        }
    }

    private static IResult Refuse(HttpContext context, int step, WizardState state, string message)
    {
        IdentityRedirectManager.SetStatus(context, StatusMessageKind.Error, message);
        return Results.LocalRedirect(WizardTexts.StepUrl(step, state));
    }
}
