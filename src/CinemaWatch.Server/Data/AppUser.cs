using Microsoft.AspNetCore.Identity;

namespace CinemaWatch.Server.Data;

/// <summary>
/// An account. The e-mail address is also the user name. An invited account has no password and an unconfirmed
/// e-mail until the invitation link is used (see <see cref="Accounts.AccountService"/>).
/// </summary>
public sealed class AppUser : IdentityUser
{
    /// <summary>"pl" or "en": language of the e-mails; the web UI switches to it after sign-in.</summary>
    [PersonalData]
    public string Language { get; set; } = "pl";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the last invitation e-mail was sent.</summary>
    public DateTimeOffset? InvitedAt { get; set; }

    /// <summary>
    /// Own profile limit set by the owner; null = the default (<see cref="Profiles.LimitOptions.MaxProfiles"/>).
    /// </summary>
    public int? MaxProfiles { get; set; }

    /// <summary>True until the invitation link has been used (no password yet).</summary>
    public bool IsPending => !EmailConfirmed;
}
