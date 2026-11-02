using System.Globalization;
using CinemaWatch.Core;
using CinemaWatch.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;

namespace CinemaWatch.Web;

/// <summary>
/// UI language per request: the language cookie (set by the PL / EN switch) wins, otherwise the browser's preferred
/// language - Polish if the browser prefers Polish, English for any other language, Polish without the header.
/// The choice is applied with <see cref="Localizer.Use"/>, so every <c>T("polski", "English")</c> in this request
/// (pages and Core texts) follows it, and also sets the culture for dates and numbers.
/// </summary>
public static class WebLanguage
{
    public const string CookieName = "cinemawatch.lang";

    public static AppLanguage Resolve(string? cookie, string? acceptLanguage)
    {
        if (Localizer.Parse(cookie) is { } chosen)
            return chosen;
        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return AppLanguage.Polish;
        if (!StringWithQualityHeaderValue.TryParseList([acceptLanguage], out var languages))
            return AppLanguage.Polish;

        var preferred = languages
            .Select((value, index) => (Tag: value.Value.Value ?? "", Quality: value.Quality ?? 1.0, index))
            .Where(l => l.Quality > 0 && l.Tag != "*")
            .OrderByDescending(l => l.Quality)
            .ThenBy(l => l.index)
            .Select(l => l.Tag.Split('-')[0].ToLowerInvariant())
            .FirstOrDefault(tag => tag is "pl" or "en");
        return preferred == "pl" || (preferred is null && languages.Count == 0) ? AppLanguage.Polish : AppLanguage.English;
    }

    /// <summary>A local path such as "/admin/users", or "/" for anything that could lead to another site.</summary>
    public static string SafeLocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
            return "/";
        if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
            return "/";
        return url.Contains('\\') ? "/" : url;
    }

    public static IApplicationBuilder UseWebLanguage(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var language = Resolve(context.Request.Cookies[CookieName], context.Request.Headers.AcceptLanguage);
            using var scope = Localizer.Use(language);
            CultureInfo.CurrentCulture = Localizer.Culture;
            CultureInfo.CurrentUICulture = Localizer.Culture;
            await next(context);
        });

    public static void SetCookie(HttpContext context, AppLanguage language) =>
        context.Response.Cookies.Append(CookieName, language == AppLanguage.English ? "en" : "pl", new CookieOptions
        {
            MaxAge = TimeSpan.FromDays(365),
            HttpOnly = true,
            IsEssential = true,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
        });

    /// <summary>
    /// After sign-in: a language chosen with the switch on this device is saved to the account (e-mails follow it);
    /// without such a choice the account's language becomes this device's language.
    /// </summary>
    public static async Task SyncAfterSignInAsync(HttpContext context, UserManager<AppUser> users, AppUser user)
    {
        if (Localizer.Parse(context.Request.Cookies[CookieName]) is { } chosen)
        {
            var code = chosen == AppLanguage.English ? "en" : "pl";
            if (user.Language != code)
            {
                user.Language = code;
                await users.UpdateAsync(user);
            }
            return;
        }
        SetCookie(context, Localizer.Parse(user.Language) ?? AppLanguage.Polish);
    }

    /// <summary>GET /language/pl?returnUrl=/x - switches the language (and the account's language when signed in).</summary>
    public static IEndpointConventionBuilder MapLanguageEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/language/{code}", async (string code, string? returnUrl, HttpContext context, UserManager<AppUser> users) =>
        {
            if (Localizer.Parse(code) is not { } language)
                return Results.NotFound();
            SetCookie(context, language);
            if (context.User.Identity?.IsAuthenticated == true && await users.GetUserAsync(context.User) is { } user)
            {
                user.Language = language == AppLanguage.English ? "en" : "pl";
                await users.UpdateAsync(user);
            }
            return Results.LocalRedirect(SafeLocalUrl(returnUrl));
        }).AllowAnonymous();
}
