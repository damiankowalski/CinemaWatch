using System.Text.Json;
using System.Text.Json.Nodes;
using CinemaWatch.Core;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using CinemaWatch.Server.Profiles;
using CinemaWatch.Web.Components.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using static CinemaWatch.Core.Localizer;

namespace Microsoft.AspNetCore.Routing;

/// <summary>
/// Form endpoints that are not pages: sign-out, the personal-data download, the test e-mail, the owner's "send again",
/// account deletion and profile limit.
/// </summary>
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
            HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, ProfileService profiles, CheckService checks, ILoggerFactory loggerFactory) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await users.GetUserAsync(context.User);
            if (user is null)
                return Results.NotFound();

            loggerFactory.CreateLogger("PersonalData").LogInformation("User {UserId} downloaded their personal data", user.Id);
            var list = await profiles.ListAsync(user.Id);
            var history = await checks.HistoryAsync(user.Id);
            var data = new JsonObject
            {
                ["id"] = user.Id,
                ["email"] = user.Email,
                ["language"] = user.Language,
                ["createdAt"] = user.CreatedAt.ToString("O"),
                ["invitedAt"] = user.InvitedAt?.ToString("O"),
                ["profileLimit"] = list.Limit,
                ["profiles"] = new JsonArray(list.Profiles.Select(p => (JsonNode)new JsonObject
                {
                    ["name"] = p.Name,
                    ["enabled"] = p.Enabled,
                    ["createdAt"] = p.CreatedAt.ToString("O"),
                    ["updatedAt"] = p.UpdatedAt.ToString("O"),
                    ["document"] = JsonNode.Parse(ProfileStore.Serialize(p.Document)),
                    ["notifications"] = new JsonArray((history.TryGetValue(p.Id, out var sent) ? sent : []).Select(n => (JsonNode)new JsonObject
                    {
                        ["at"] = n.At.ToString("O"),
                        ["delivered"] = n.Delivered,
                        ["subject"] = n.Subject,
                        ["summary"] = n.Summary,
                    }).ToArray()),
                }).ToArray()),
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Polish letters stay readable
            });
            return Results.File(json, "application/json", "cinemawatch-personal-data.json");
        });

        endpoints.MapPost("/Account/Manage/TestMail", async (
            HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, CheckService checks, Microsoft.Extensions.Options.IOptions<CinemaWatch.Server.Mail.MailOptions> mail) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await users.GetUserAsync(context.User);
            if (user is null)
                return Results.NotFound();
            var result = await checks.SendTestMailAsync(user.Id);
            var (kind, message) = result.Outcome switch
            {
                TestMailOutcome.Sent when mail.Value.IsLocal => (StatusMessageKind.Success,
                    T($"Testowy e-mail do {user.Email} trafił do lokalnej skrzynki testowej na tym komputerze (tryb testowy): do prawdziwej skrzynki nie wychodzi.",
                      $"The test e-mail to {user.Email} went to the local test inbox on this computer (test mode): it does not reach the real mailbox.")),
                TestMailOutcome.Sent => (StatusMessageKind.Success,
                    T($"Testowy e-mail wysłany na {user.Email}. Sprawdź skrzynkę i folder spam.", $"Test e-mail sent to {user.Email}. Check your inbox and the spam folder.")),
                TestMailOutcome.TooSoon => (StatusMessageKind.Error,
                    T($"Testowy e-mail wyszedł przed chwilą. Kolejny możesz wysłać o {PollerTime.ClockText(result.AvailableAt!.Value)}.",
                      $"A test e-mail went out a moment ago. You can send another one at {PollerTime.ClockText(result.AvailableAt!.Value)}.")),
                _ => (StatusMessageKind.Error, T($"E-mail nie wyszedł: {result.Error}", $"The e-mail was not sent: {result.Error}")),
            };
            IdentityRedirectManager.SetStatus(context, kind, message);
            return Results.LocalRedirect("/Account/Manage");
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

        endpoints.MapPost("/admin/users/delete", async ([FromForm] string userId, HttpContext context, AccountService accounts) =>
        {
            // Form-bound minimal endpoints check the antiforgery token automatically; the owner policy is enforced here,
            // and the service refuses the owner's own account whatever the form says.
            var result = await accounts.DeleteByOwnerAsync(userId);
            var (kind, message) = AdminMessages.Describe(result);
            IdentityRedirectManager.SetStatus(context, kind, message);
            return TypedResults.LocalRedirect("/admin/users");
        }).RequireAuthorization(CinemaWatch.Web.OwnerAuthorizationHandler.Policy);

        endpoints.MapPost("/admin/users/limit", async (
            [FromForm] string userId, [FromForm] string? maxProfiles, HttpContext context, UserManager<AppUser> users, ProfileService profiles) =>
        {
            // Form-bound minimal endpoints check the antiforgery token automatically. An empty field means the default.
            var email = (await users.FindByIdAsync(userId))?.Email ?? "";
            int? limit = null;
            var isNumberOrEmpty = string.IsNullOrWhiteSpace(maxProfiles);
            if (!isNumberOrEmpty && int.TryParse(maxProfiles!.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                limit = parsed;
                isNumberOrEmpty = true;
            }
            var change = isNumberOrEmpty ? await profiles.SetLimitAsync(userId, limit) : LimitChange.OutOfRange;

            var (kind, message) = change switch
            {
                LimitChange.Saved when limit is null => (StatusMessageKind.Success,
                    T($"Limit profili dla {email}: domyślny ({profiles.Limits.MaxProfiles}).", $"Profile limit of {email}: the default ({profiles.Limits.MaxProfiles}).")),
                LimitChange.Saved => (StatusMessageKind.Success, T($"Limit profili dla {email}: {limit}.", $"Profile limit of {email}: {limit}.")),
                LimitChange.NotFound => (StatusMessageKind.Error, T("Nie ma takiego konta.", "There is no such account.")),
                _ => (StatusMessageKind.Error, T($"Limit musi być liczbą od 1 do {LimitOptions.ProfileLimitCeiling}.", $"The limit must be a number from 1 to {LimitOptions.ProfileLimitCeiling}.")),
            };
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

    public static (StatusMessageKind Kind, string Message) Describe(AccountDeletionResult result) => result.Outcome switch
    {
        AccountDeletionOutcome.Deleted when result.WasPending => (StatusMessageKind.Success,
            T($"Zaproszenie dla {result.Email} wycofane.", $"The invitation for {result.Email} was withdrawn.")),
        AccountDeletionOutcome.Deleted => (StatusMessageKind.Success,
            T($"Konto {result.Email} usunięte (profile: {result.Profiles}). Ta osoba dostała e-mail.",
              $"The account {result.Email} was deleted (profiles: {result.Profiles}). The person got an e-mail.")),
        AccountDeletionOutcome.DeletedMailFailed => (StatusMessageKind.Error,
            T($"Konto {result.Email} usunięte (profile: {result.Profiles}), ale e-mail do tej osoby nie wyszedł: {string.Join(" ", result.Errors)}",
              $"The account {result.Email} was deleted (profiles: {result.Profiles}), but the e-mail to the person was not sent: {string.Join(" ", result.Errors)}")),
        AccountDeletionOutcome.NotFound => (StatusMessageKind.Error, T("Nie ma takiego konta.", "There is no such account.")),
        AccountDeletionOutcome.OwnerAccount => (StatusMessageKind.Error,
            T("Konta właściciela nie można usunąć w tym miejscu.", "The owner's account cannot be deleted here.")),
        _ => (StatusMessageKind.Error, string.Join(" ", result.Errors)),
    };
}
