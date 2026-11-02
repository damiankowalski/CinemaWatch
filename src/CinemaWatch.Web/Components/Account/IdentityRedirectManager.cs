using Microsoft.AspNetCore.Components;

namespace CinemaWatch.Web.Components.Account;

/// <summary>Redirects from page handlers, optionally with a one-time message for the next page (a short-lived cookie).</summary>
public sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public const string StatusCookieName = "cinemawatch.status";

    private static readonly CookieBuilder StatusCookieBuilder = new()
    {
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// Redirects to a local path only ("Account/Login" or "/Account/Login"); anything that could lead to another site
    /// goes to the home page (no open redirects).
    /// </summary>
    public void RedirectTo(string? uri) => navigationManager.NavigateTo(Normalize(uri));

    /// <summary>App-relative paths get their leading slash; absolute addresses ("https://...") are refused.</summary>
    public static string Normalize(string? uri) =>
        WebLanguage.SafeLocalUrl(!string.IsNullOrEmpty(uri) && uri[0] != '/' && !uri.Contains(':') ? "/" + uri : uri);

    public void RedirectToWithStatus(string uri, StatusMessageKind kind, string message, HttpContext context)
    {
        SetStatus(context, kind, message);
        RedirectTo(uri);
    }

    public void RedirectToCurrentPageWithStatus(StatusMessageKind kind, string message, HttpContext context) =>
        RedirectToWithStatus("/" + navigationManager.ToBaseRelativePath(navigationManager.Uri), kind, message, context);

    public static void SetStatus(HttpContext context, StatusMessageKind kind, string message) =>
        context.Response.Cookies.Append(StatusCookieName, $"{(kind == StatusMessageKind.Error ? "error" : "ok")}|{message}", StatusCookieBuilder.Build(context));
}

public enum StatusMessageKind
{
    Success,
    Error,
}
