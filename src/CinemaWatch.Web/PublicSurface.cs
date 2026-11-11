using System.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

namespace CinemaWatch.Web;

/// <summary>
/// What a visitor without an account meets (docs/SAAS.md §9: such a visitor can cause no cost and no damage):
/// no search engine indexing, and <c>/admin</c> only exactly as the Cloudflare Access application for the owner's
/// pages is written. ASP.NET Core ignores the case of a path and decodes it, a path rule at the edge may not:
/// <c>/ADMIN</c>, <c>/%61dmin</c> or <c>//admin</c> must never reach the owner's pages past that application.
/// </summary>
public static class PublicSurface
{
    public const string AdminPrefix = "/admin";

    public const string RobotsText = "User-agent: *\nDisallow: /\n";

    public static IApplicationBuilder UsePublicSurface(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                return Task.CompletedTask;
            });

            var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            if (!IsAdminPathAsWritten(context.Request.Path, rawTarget))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });

    public const string InvalidFormText =
        "Formularz wygasł. Wróć, odśwież stronę i spróbuj ponownie.\nThe form has expired. Go back, reload the page and try again.\n";

    /// <summary>
    /// A form posted without a valid antiforgery token gets a short 400 answer before any page or endpoint reads it.
    /// After <c>UseAntiforgery</c>, which only records the result. The status code page must not run for it: running
    /// the pipeline again for the same post would read the refused form and throw (an unhandled 500).
    /// </summary>
    public static IApplicationBuilder UseInvalidFormRefusal(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false })
            {
                if (context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } statusPages)
                    statusPages.Enabled = false;
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(InvalidFormText);
                return;
            }
            await next(context);
        });

    /// <summary>GET /robots.txt: nothing on this site is for search engines.</summary>
    public static IEndpointConventionBuilder MapRobots(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/robots.txt", () => Results.Text(RobotsText, "text/plain")).AllowAnonymous();

    /// <summary>
    /// False for a request that the app would route to the owner's pages (first path segment "admin" in any case,
    /// after decoding) although the address as sent is not exactly <c>/admin</c> or <c>/admin/…</c>.
    /// </summary>
    public static bool IsAdminPathAsWritten(PathString path, string? rawTarget)
    {
        var decoded = path.Value ?? "";
        var firstSegment = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.Equals(firstSegment, "admin", StringComparison.OrdinalIgnoreCase))
            return true;

        var raw = string.IsNullOrEmpty(rawTarget) ? decoded : rawTarget;
        // An absolute-form target (http://host/admin) is compared by its path, as sent (System.Uri would decode it).
        var scheme = raw.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0 && !raw.StartsWith('/'))
        {
            var pathStart = raw.IndexOf('/', scheme + 3);
            raw = pathStart < 0 ? "/" : raw[pathStart..];
        }
        var rawPath = raw.Split('?', 2)[0];
        return rawPath == AdminPrefix || rawPath.StartsWith(AdminPrefix + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Waits until at least <paramref name="minimumMilliseconds"/> have passed since <paramref name="startedTimestamp"/>
    /// (<see cref="Stopwatch.GetTimestamp"/>), so an answer takes the same time whatever the server found.
    /// </summary>
    public static async Task PadAnswerAsync(long startedTimestamp, int minimumMilliseconds, CancellationToken cancellationToken = default)
    {
        // A timer may wake up a fraction of a millisecond early (Windows' timer resolution), so check again.
        var minimum = TimeSpan.FromMilliseconds(minimumMilliseconds);
        TimeSpan remaining;
        while ((remaining = minimum - Stopwatch.GetElapsedTime(startedTimestamp)) > TimeSpan.Zero)
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining, cancellationToken);
    }
}
