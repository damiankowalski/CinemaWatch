using System.Net.Mail;
using static CinemaWatch.Core.Localizer;

namespace CinemaWatch.Web.Components.Account;

/// <summary>
/// Form checks with Polish and English messages. The browser checks the same things first (required, type=email,
/// minlength); these run on the server because a form can be sent without a browser.
/// </summary>
internal static class FormChecks
{
    public static bool IsEmail(string? value)
    {
        value = value?.Trim();
        return !string.IsNullOrEmpty(value) && MailAddress.TryCreate(value, out var address) && address.Address == value;
    }

    public static string? Email(string? value) =>
        IsEmail(value) ? null : T("Podaj poprawny adres e-mail.", "Enter a valid e-mail address.");

    public static IReadOnlyList<string> NewPassword(string? password, string? repeated)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password))
            errors.Add(T("Podaj nowe hasło.", "Enter a new password."));
        else if (password != repeated)
            errors.Add(T("Hasła się różnią.", "The passwords do not match."));
        return errors;
    }
}
