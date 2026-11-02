using System.Text.Json;
using CinemaWatch.Core;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Web.Components.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using static CinemaWatch.Core.Localizer;

namespace Microsoft.AspNetCore.Routing;

/// <summary>Form endpoints that are not pages: sign-out, the personal-data download and the owner's "send again".</summary>
internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/Account/Logout", async (HttpContext context, IAntiforgery antiforgery, SignInManager<AppUser> signInManager) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await signInManager.SignOutAsync();
            IdentityRedirectManager.SetStatus(context, StatusMessageKind.Success, T("Wylogowano.", "You have signed out."));
            return TypedResults.LocalRedirect("/Account/Login");
        });

        endpoints.MapPost("/Account/Manage/DownloadPersonalData", async (
            HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, ILoggerFactory loggerFactory) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await users.GetUserAsync(context.User);
            if (user is null)
                return Results.NotFound();

            loggerFactory.CreateLogger("PersonalData").LogInformation("User {UserId} downloaded their personal data", user.Id);
            var data = new Dictionary<string, string?>
            {
                ["id"] = user.Id,
                ["email"] = user.Email,
                ["language"] = user.Language,
                ["createdAt"] = user.CreatedAt.ToString("O"),
                ["invitedAt"] = user.InvitedAt?.ToString("O"),
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true });
            return Results.File(json, "application/json", "cinemawatch-personal-data.json");
        });

        endpoints.MapPost("/admin/users/resend", async (
            [FromForm] string email, HttpContext context, AccountService accounts) =>
        {
            // Form-bound minimal endpoints check the antiforgery token automatically.
            var result = await accounts.InviteAsync(email, AppLanguage.Polish);
            var (kind, message) = AdminMessages.Describe(result, email);
            IdentityRedirectManager.SetStatus(context, kind, message);
            return TypedResults.LocalRedirect("/admin/users");
        }).RequireAuthorization(CinemaWatch.Web.OwnerAuthorizationHandler.Policy);
    }
}

/// <summary>Texts for the outcome of an invitation (owner's page and the "send again" button).</summary>
internal static class AdminMessages
{
    public static (StatusMessageKind Kind, string Message) Describe(InvitationResult result, string email) => result.Outcome switch
    {
        InvitationOutcome.Created => (StatusMessageKind.Success, T($"Zaproszenie wysłane na {email}.", $"Invitation sent to {email}.")),
        InvitationOutcome.Resent => (StatusMessageKind.Success, T($"Konto {email} czekało na aktywację: wysłano nowe zaproszenie.", $"The account {email} was waiting for activation: a new invitation was sent.")),
        InvitationOutcome.AlreadyActive => (StatusMessageKind.Error, T($"Konto {email} jest już aktywne.", $"The account {email} is already active.")),
        InvitationOutcome.MailFailed => (StatusMessageKind.Error, T($"Konto {email} czeka na aktywację, ale e-mail nie wyszedł: {string.Join(" ", result.Errors)}", $"The account {email} waits for activation, but the e-mail was not sent: {string.Join(" ", result.Errors)}")),
        _ => (StatusMessageKind.Error, string.Join(" ", result.Errors)),
    };
}
