using CinemaWatch.Server.Data;
using CinemaWatch.Server.Operations;
using CinemaWatch.Web;
using CinemaWatch.Web.Components.Account;
using Microsoft.AspNetCore.Antiforgery;
using static CinemaWatch.Core.Localizer;

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// Form endpoints of the operator's panel (owner only): pause and resume all checks, restore the default poller
/// settings. Saving the settings is a form on the page itself (Components/Admin/Operator.razor), so its errors can be
/// shown next to the fields. Every owner page and form lives under /admin, so one rule (Cloudflare Access on /admin*,
/// stage 4) can guard them all.
/// </summary>
internal static class OperatorEndpoints
{
    public const string PanelPath = "/admin";

    public static void MapOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/pause", async (HttpContext context, IAntiforgery antiforgery, OperatorService operations) =>
            await ActAsync(context, antiforgery, async () => await operations.PauseAsync()
                ? (StatusMessageKind.Success, T("Sprawdzanie wstrzymane. Od następnego cyklu serwer nie wysyła zapytań do Cinema City, a użytkownicy widzą komunikat.",
                                                "Checks paused. From the next cycle the server sends no requests to Cinema City, and users see a notice."))
                : (StatusMessageKind.Error, T("Sprawdzanie jest już wstrzymane.", "Checks are already paused."))))
            .RequireAuthorization(OwnerAuthorizationHandler.Policy);

        endpoints.MapPost("/admin/resume", async (HttpContext context, IAntiforgery antiforgery, OperatorService operations) =>
            await ActAsync(context, antiforgery, async () => await operations.ResumeAsync()
                ? (StatusMessageKind.Success, T("Sprawdzanie wznowione.", "Checks resumed."))
                : (StatusMessageKind.Error, T("Sprawdzanie nie było wstrzymane.", "Checks were not paused."))))
            .RequireAuthorization(OwnerAuthorizationHandler.Policy);

        endpoints.MapPost("/admin/settings/defaults", async (HttpContext context, IAntiforgery antiforgery, OperatorService operations) =>
            await ActAsync(context, antiforgery, async () =>
            {
                var result = await operations.SaveSettingsAsync(new PollerSettingsRow());
                return result.Changes.Count == 0
                    ? (StatusMessageKind.Success, T("Ustawienia już są takie jak w konfiguracji.", "The settings already match the configuration."))
                    : (StatusMessageKind.Success, T($"Przywrócono ustawienia z konfiguracji (zmiany: {result.Changes.Count}). Działają od następnego cyklu.",
                                                    $"Settings restored from the configuration (changes: {result.Changes.Count}). They apply from the next cycle."));
            }))
            .RequireAuthorization(OwnerAuthorizationHandler.Policy);
    }

    /// <summary>Runs an action and goes back to the top of the panel, where the message is shown.</summary>
    private static async Task<IResult> ActAsync(
        HttpContext context, IAntiforgery antiforgery, Func<Task<(StatusMessageKind Kind, string Message)>> action)
    {
        // These forms carry no fields, so the token is checked here (form-bound endpoints check it automatically).
        if (!await antiforgery.IsRequestValidAsync(context))
            return Results.BadRequest();
        var (kind, message) = await action();
        IdentityRedirectManager.SetStatus(context, kind, message);
        return Results.LocalRedirect(PanelPath);
    }
}
