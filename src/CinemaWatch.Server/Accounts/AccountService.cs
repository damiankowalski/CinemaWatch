using System.Buffers.Text;
using System.Text;
using CinemaWatch.Core;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Accounts;

public enum InvitationOutcome
{
    /// <summary>A new account was created and the invitation sent.</summary>
    Created,

    /// <summary>The account was still waiting for its first password; a new invitation was sent.</summary>
    Resent,

    /// <summary>The account is already active; nothing was sent.</summary>
    AlreadyActive,

    /// <summary>The address was refused (see the errors).</summary>
    Failed,

    /// <summary>The account exists and waits for its first password, but the e-mail could not be sent (see the errors).</summary>
    MailFailed,
}

public sealed record InvitationResult(InvitationOutcome Outcome, IReadOnlyList<string> Errors);

/// <summary>One row of the owner's account list.</summary>
public sealed record AccountSummary(string Id, string Email, bool IsPending, string Language, DateTimeOffset CreatedAt, DateTimeOffset? InvitedAt);

/// <summary>
/// Accounts by invitation only (docs/SAAS.md §5.1): the owner invites an e-mail address, the invited person sets a
/// password through the e-mail link, which also confirms the address. Invitation and password-reset links use the
/// same one-time token and the same page; they differ only in the wording.
/// </summary>
public sealed class AccountService(
    UserManager<AppUser> users,
    IMailSender mail,
    IOptions<SaasOptions> options,
    TimeProvider time,
    ILogger<AccountService> logger)
{
    /// <summary>How long an invitation or password-reset link works (Identity token lifetime).</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    /// <summary>Path of the page that sets a password from an e-mail link.</summary>
    public const string SetPasswordPath = "/Account/SetPassword";

    public async Task<InvitationResult> InviteAsync(string email, AppLanguage language, CancellationToken cancellationToken = default)
    {
        email = email.Trim();
        var user = await users.FindByEmailAsync(email);
        var outcome = InvitationOutcome.Resent;
        if (user is null)
        {
            user = new AppUser
            {
                UserName = email,
                Email = email,
                Language = language == AppLanguage.English ? "en" : "pl",
                CreatedAt = time.GetUtcNow(),
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
                return new InvitationResult(InvitationOutcome.Failed, created.Errors.Select(e => e.Description).ToList());
            outcome = InvitationOutcome.Created;
        }
        else if (!user.IsPending)
        {
            return new InvitationResult(InvitationOutcome.AlreadyActive, []);
        }

        try
        {
            await SendInvitationAsync(user, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The account stays; "send again" (owner) or "Forgot your password?" (the person) retries the e-mail.
            logger.LogError(ex, "Invitation e-mail for user {UserId} could not be sent", user.Id);
            return new InvitationResult(InvitationOutcome.MailFailed, [ex.Message]);
        }
        logger.LogInformation("Invitation {Outcome} for user {UserId}", outcome, user.Id);
        return new InvitationResult(outcome, []);
    }

    /// <summary>All accounts, oldest first (owner's view).</summary>
    public async Task<IReadOnlyList<AccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        (await users.Users.ToListAsync(cancellationToken))
            .OrderBy(u => u.CreatedAt)
            .Select(u => new AccountSummary(u.Id, u.Email ?? "", u.IsPending, u.Language, u.CreatedAt, u.InvitedAt))
            .ToList();

    /// <summary>
    /// "Forgot your password?": an active account gets a reset link, an account still waiting for its first password
    /// gets a new invitation (e.g. after the first link expired), an unknown address gets nothing. The caller shows
    /// the same answer in every case, so the page does not reveal which addresses have an account.
    /// </summary>
    public async Task RequestPasswordHelpAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
            return;
        if (user.IsPending)
        {
            await SendInvitationAsync(user, cancellationToken);
            return;
        }

        var link = await BuildSetPasswordLinkAsync(user);
        using (Localizer.Use(LanguageOf(user)))
            await mail.SendAsync(AccountMails.PasswordReset(user.Email!, link, LinkLifetime), cancellationToken);
    }

    /// <summary>The account behind a set-password link, or null for an unknown id.</summary>
    public Task<AppUser?> FindAsync(string? userId) =>
        string.IsNullOrEmpty(userId) ? Task.FromResult<AppUser?>(null) : users.FindByIdAsync(userId);

    /// <summary>Checks a set-password link without using it up, so the page can say "expired" before anyone types a password.</summary>
    public async Task<bool> IsSetPasswordLinkValidAsync(AppUser user, string? encodedToken) =>
        encodedToken is not null
        && TryDecode(encodedToken, out var token)
        && await users.VerifyUserTokenAsync(user, users.Options.Tokens.PasswordResetTokenProvider, UserManager<AppUser>.ResetPasswordTokenPurpose, token);

    /// <summary>
    /// Sets the password from an e-mail link. For an invited account this also confirms the e-mail address (the person
    /// has just proved they can read it) and activates the account.
    /// </summary>
    public async Task<IdentityResult> SetPasswordAsync(string userId, string encodedToken, string password)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null || !TryDecode(encodedToken, out var token))
            return IdentityResult.Failed(users.ErrorDescriber.InvalidToken());

        var result = await users.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded || !user.IsPending)
            return result;

        user.EmailConfirmed = true;
        return await users.UpdateAsync(user);
    }

    /// <summary>Creates and invites the owner's account at start-up if it does not exist yet. Safe to call on every start.</summary>
    public async Task EnsureOwnerAsync(CancellationToken cancellationToken = default)
    {
        var ownerEmail = options.Value.OwnerEmail.Trim();
        if (ownerEmail.Length == 0)
        {
            logger.LogWarning("Saas:OwnerEmail is not set: nobody can invite accounts");
            return;
        }
        if (await users.FindByEmailAsync(ownerEmail) is not null)
            return;

        var result = await InviteAsync(ownerEmail, AppLanguage.Polish, cancellationToken);
        switch (result.Outcome)
        {
            case InvitationOutcome.Failed:
                throw new InvalidOperationException($"The owner's account could not be created: {string.Join("; ", result.Errors)}");
            case InvitationOutcome.MailFailed:
                logger.LogError("Owner account created, but the invitation e-mail failed. Start the mail server, then use \"Forgot your password?\" on the sign-in page to get the link");
                break;
            default:
                logger.LogInformation("Owner account created; the invitation e-mail has been sent");
                break;
        }
    }

    /// <summary>Whether this e-mail belongs to the owner (case-insensitive).</summary>
    public static bool IsOwnerEmail(string? email, SaasOptions options) =>
        !string.IsNullOrWhiteSpace(email)
        && !string.IsNullOrWhiteSpace(options.OwnerEmail)
        && string.Equals(email.Trim(), options.OwnerEmail.Trim(), StringComparison.OrdinalIgnoreCase);

    public static AppLanguage LanguageOf(AppUser user) => Localizer.Parse(user.Language) ?? AppLanguage.Polish;

    private async Task SendInvitationAsync(AppUser user, CancellationToken cancellationToken)
    {
        var link = await BuildSetPasswordLinkAsync(user);
        using (Localizer.Use(LanguageOf(user)))
            await mail.SendAsync(AccountMails.Invitation(user.Email!, link, LinkLifetime), cancellationToken);
        user.InvitedAt = time.GetUtcNow();
        await users.UpdateAsync(user);
    }

    private async Task<string> BuildSetPasswordLinkAsync(AppUser user)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        return BuildSetPasswordLink(options.Value.BaseUrl, user.Id, token);
    }

    /// <summary>{BaseUrl}/Account/SetPassword?user={id}&amp;code={token as Base64Url}</summary>
    public static string BuildSetPasswordLink(string baseUrl, string userId, string token)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Saas:BaseUrl must be an absolute http(s) address, e.g. http://localhost:5180");
        var code = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
        return $"{root.GetLeftPart(UriPartial.Authority)}{SetPasswordPath}?user={Uri.EscapeDataString(userId)}&code={code}";
    }

    private static bool TryDecode(string encoded, out string token)
    {
        try
        {
            token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded));
            return true;
        }
        catch (FormatException)
        {
            token = "";
            return false;
        }
    }
}
