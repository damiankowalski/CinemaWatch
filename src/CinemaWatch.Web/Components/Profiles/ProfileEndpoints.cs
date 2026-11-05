using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using CinemaWatch.Server.Profiles;
using CinemaWatch.Web.Components.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using static CinemaWatch.Core.Localizer;

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// Form endpoints of the profile list (enable, disable, delete, check now) and the download. The import is a form on
/// the page itself (Components/Pages/Home.razor), so its results can be shown in full.
/// </summary>
internal static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/profiles/{id:guid}/enable", async (Guid id, HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, ProfileService profiles) =>
            await ChangeAsync(context, antiforgery, users, ProfilePage(id, await OpenAsync(context)), async userId => await profiles.SetEnabledAsync(userId, id, true) is { } name
                ? T($"Profil „{name}” włączony.", $"Profile \"{name}\" enabled.")
                : null));

        endpoints.MapPost("/profiles/{id:guid}/disable", async (Guid id, HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, ProfileService profiles) =>
            await ChangeAsync(context, antiforgery, users, ProfilePage(id, await OpenAsync(context)), async userId => await profiles.SetEnabledAsync(userId, id, false) is { } name
                ? T($"Profil „{name}” wyłączony.", $"Profile \"{name}\" disabled.")
                : null));

        endpoints.MapPost("/profiles/{id:guid}/delete", async (Guid id, HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, ProfileService profiles) =>
            await ChangeAsync(context, antiforgery, users, ProfilePage(null, await OpenAsync(context)), async userId => await profiles.DeleteAsync(userId, id) is { } name
                ? T($"Profil „{name}” usunięty.", $"Profile \"{name}\" deleted.")
                : null));

        endpoints.MapPost("/profiles/{id:guid}/check", async (Guid id, HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, CheckService checks) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            var userId = users.GetUserId(context.User);
            if (userId is null)
                return Results.NotFound();
            var result = await checks.RequestCheckAsync(userId, id);
            var name = result.Name;
            var at = result.Until is { } until ? PollerTime.ClockText(until) : "";
            var (kind, message) = result.Outcome switch
            {
                CheckRequestOutcome.Requested => (StatusMessageKind.Success,
                    T($"Sprawdzam profil „{name}”. Wyniki pojawiają się seans po seansie – strona odświeża się sama.", $"Checking the profile \"{name}\". The results appear screening by screening - the page refreshes itself.")),
                CheckRequestOutcome.AlreadyChecking => (StatusMessageKind.Error,
                    T($"Profil „{name}” jest właśnie sprawdzany.", $"The profile \"{name}\" is being checked right now.")),
                CheckRequestOutcome.Paused when result.Until is null => (StatusMessageKind.Error,
                    T("Sprawdzanie jest wstrzymane przez właściciela serwisu.", "Checks are paused by the site's owner.")),
                CheckRequestOutcome.Paused => (StatusMessageKind.Error,
                    T($"Sprawdzanie jest wstrzymane do {at}: Cinema City odrzuciło zapytania serwera.", $"Checks are paused until {at}: Cinema City refused the server's requests.")),
                CheckRequestOutcome.NotActive => (StatusMessageKind.Error,
                    T($"Profil „{name}” jest wyłączony albo jego seanse już się zaczęły.", $"The profile \"{name}\" is disabled or its screenings have started.")),
                _ => (StatusMessageKind.Error, ""),
            };
            if (result.Outcome == CheckRequestOutcome.NotFound)
                return Results.NotFound();
            IdentityRedirectManager.SetStatus(context, kind, message);
            return Results.LocalRedirect(ProfilePage(id, await OpenAsync(context)));
        });

        endpoints.MapGet("/profiles/{id:guid}/download", async (Guid id, HttpContext context, UserManager<AppUser> users, ProfileService profiles) =>
        {
            var userId = users.GetUserId(context.User);
            return userId is not null && await profiles.DownloadAsync(userId, id) is { } download
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(download.Json), "application/json", download.FileName)
                : Results.NotFound();
        });
    }

    /// <summary>
    /// The list with this profile opened and scrolled into view, and the profiles that were open before still open
    /// (the form's "open" field: ids separated by commas; anything that is not an id is dropped, the page shows only
    /// the account's own profiles anyway).
    /// </summary>
    private static string ProfilePage(Guid? id, string? open)
    {
        var others = (open ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(LimitOptions.ProfileLimitCeiling)
            .Select(part => Guid.TryParse(part, out var other) ? other : Guid.Empty)
            .Where(other => other != Guid.Empty && other != id)
            .Distinct()
            .ToList();
        var query = new List<string>();
        if (id is { } focus)
            query.Add($"profile={focus}");
        if (others.Count > 0)
            query.Add($"open={string.Join(",", others)}");
        return (query.Count == 0 ? "/" : "/?" + string.Join("&", query)) + (id is { } anchor ? $"#profile-{anchor}" : "");
    }

    /// <summary>The form's "open" field (the other open profiles), read after the antiforgery check read the form.</summary>
    private static async Task<string?> OpenAsync(HttpContext context) =>
        context.Request.HasFormContentType ? (await context.Request.ReadFormAsync()).TryGetValue("open", out var value) ? value.ToString() : null : null;

    /// <summary>
    /// Runs a change for the signed-in account and goes back to the list with a message. The change returns the
    /// message, or null when the account has no such profile (someone else's id looks exactly like a missing one).
    /// </summary>
    private static async Task<IResult> ChangeAsync(
        HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, string returnTo, Func<string, Task<string?>> change)
    {
        // These forms carry no fields, so the token is checked here (form-bound endpoints check it automatically).
        if (!await antiforgery.IsRequestValidAsync(context))
            return Results.BadRequest();
        var userId = users.GetUserId(context.User);
        if (userId is null || await change(userId) is not { } message)
            return Results.NotFound();
        IdentityRedirectManager.SetStatus(context, StatusMessageKind.Success, message);
        return Results.LocalRedirect(returnTo);
    }
}
