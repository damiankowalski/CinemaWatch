using CinemaWatch.Server.Data;
using CinemaWatch.Server.Profiles;
using CinemaWatch.Web.Components.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using static CinemaWatch.Core.Localizer;

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// Form endpoints of the profile list (enable, disable, delete) and the download. The import is a form on the page
/// itself (Components/Pages/Home.razor), so its results can be shown in full.
/// </summary>
internal static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/profiles/{id:guid}/enable", (Guid id, HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, ProfileService profiles) =>
            ChangeAsync(context, antiforgery, users, ProfilePage(id), async userId => await profiles.SetEnabledAsync(userId, id, true) is { } name
                ? T($"Profil „{name}” włączony.", $"Profile \"{name}\" enabled.")
                : null));

        endpoints.MapPost("/profiles/{id:guid}/disable", (Guid id, HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, ProfileService profiles) =>
            ChangeAsync(context, antiforgery, users, ProfilePage(id), async userId => await profiles.SetEnabledAsync(userId, id, false) is { } name
                ? T($"Profil „{name}” wyłączony.", $"Profile \"{name}\" disabled.")
                : null));

        endpoints.MapPost("/profiles/{id:guid}/delete", (Guid id, HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, ProfileService profiles) =>
            ChangeAsync(context, antiforgery, users, "/", async userId => await profiles.DeleteAsync(userId, id) is { } name
                ? T($"Profil „{name}” usunięty.", $"Profile \"{name}\" deleted.")
                : null));

        endpoints.MapGet("/profiles/{id:guid}/download", async (Guid id, HttpContext context, UserManager<AppUser> users, ProfileService profiles) =>
        {
            var userId = users.GetUserId(context.User);
            return userId is not null && await profiles.DownloadAsync(userId, id) is { } download
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(download.Json), "application/json", download.FileName)
                : Results.NotFound();
        });
    }

    /// <summary>The list with this profile opened and scrolled into view.</summary>
    private static string ProfilePage(Guid id) => $"/?profile={id}#profile-{id}";

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
