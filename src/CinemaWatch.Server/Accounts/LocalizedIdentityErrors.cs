using Microsoft.AspNetCore.Identity;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Server.Accounts;

/// <summary>Polish and English texts for the Identity errors the pages can show (the rest keep Identity's English).</summary>
public sealed class LocalizedIdentityErrors : IdentityErrorDescriber
{
    public override IdentityError DefaultError() =>
        Error(nameof(DefaultError), T("Wystąpił nieznany błąd.", "An unknown error occurred."));

    public override IdentityError InvalidToken() =>
        Error(nameof(InvalidToken), T("Link jest nieważny albo wygasł. Poproś o nowy na stronie logowania („Nie pamiętasz hasła?”).",
            "The link is invalid or has expired. Ask for a new one on the sign-in page (“Forgot your password?”)."));

    public override IdentityError PasswordMismatch() =>
        Error(nameof(PasswordMismatch), T("Obecne hasło jest nieprawidłowe.", "The current password is incorrect."));

    public override IdentityError PasswordTooShort(int length) =>
        Error(nameof(PasswordTooShort), T($"Hasło musi mieć co najmniej {length} znaków.", $"The password must have at least {length} characters."));

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        Error(nameof(PasswordRequiresUniqueChars), T($"Hasło musi zawierać co najmniej {uniqueChars} różnych znaków.", $"The password must contain at least {uniqueChars} different characters."));

    public override IdentityError InvalidEmail(string? email) =>
        Error(nameof(InvalidEmail), T("To nie jest poprawny adres e-mail.", "This is not a valid e-mail address."));

    public override IdentityError InvalidUserName(string? userName) =>
        Error(nameof(InvalidUserName), T("To nie jest poprawny adres e-mail.", "This is not a valid e-mail address."));

    public override IdentityError DuplicateEmail(string email) =>
        Error(nameof(DuplicateEmail), T("Konto z tym adresem już istnieje.", "An account with this address already exists."));

    public override IdentityError DuplicateUserName(string userName) =>
        Error(nameof(DuplicateUserName), T("Konto z tym adresem już istnieje.", "An account with this address already exists."));

    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };
}
