using CinemaWatch.Server;
using CinemaWatch.Server.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Web;

/// <summary>
/// Running on the server behind Cloudflare Tunnel (docs/SAAS.md §4.4): visitors use HTTPS up to Cloudflare, but the
/// request reaches the app over plain HTTP from <c>cloudflared</c> in the container network. The settings are read when
/// the options are first used, so a test can change them like any other configuration value.
/// </summary>
internal static class BehindProxy
{
    /// <summary>Address of the health check for the deployment and the container test; anonymous, no personal data.</summary>
    public const string HealthPath = "/healthz";

    public static IServiceCollection AddBehindProxy(this IServiceCollection services)
    {
        services.AddSingleton<IConfigureOptions<ForwardedHeadersOptions>, ForwardedHeadersSetup>();
        // Named options: every cookie of Identity (application, external, two-factor), not only the sign-in cookie.
        services.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, SecureCookiesSetup>();
        services.AddSingleton<IConfigureOptions<AntiforgeryOptions>, SecureCookiesSetup>();
        return services;
    }

    /// <summary>
    /// "ok" (200) when the database answers, 503 otherwise. Used by the container test in CI and the deployment step on
    /// the server; reveals nothing but that.
    /// </summary>
    public static void MapHealthCheck(this WebApplication app) =>
        app.MapGet(HealthPath, async (AppDbContext db, CancellationToken cancellationToken) =>
                await db.Database.CanConnectAsync(cancellationToken)
                    ? Results.Text("ok")
                    : Results.Text("database unavailable", statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous();

    /// <summary>
    /// <c>Saas:TrustForwardedHeaders</c> on: the scheme and the client address come from <c>X-Forwarded-Proto</c> and
    /// <c>X-Forwarded-For</c> of any sender, because only the proxy can reach the app. Off: the headers are ignored.
    /// </summary>
    private sealed class ForwardedHeadersSetup(IOptions<SaasOptions> saas) : IConfigureOptions<ForwardedHeadersOptions>
    {
        public void Configure(ForwardedHeadersOptions options)
        {
            if (!saas.Value.TrustForwardedHeaders)
            {
                options.ForwardedHeaders = ForwardedHeaders.None;
                return;
            }
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
            // The proxy's address in the container network is not fixed; the network itself is the boundary.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }
    }

    /// <summary>
    /// An https <c>Saas:BaseUrl</c> makes the sign-in cookies Secure always; otherwise (development over
    /// http://localhost) they follow the request. The form (antiforgery) cookie always follows the request: with
    /// <c>Always</c> ASP.NET Core refuses to render any form on a request that is not HTTPS, so a missing
    /// <c>X-Forwarded-Proto</c> would break every page instead of only dropping a flag. Behind the trusted tunnel the
    /// request is HTTPS, so the form cookie is Secure there too.
    /// </summary>
    private sealed class SecureCookiesSetup(IOptions<SaasOptions> saas)
        : IConfigureNamedOptions<CookieAuthenticationOptions>, IConfigureOptions<AntiforgeryOptions>
    {
        public void Configure(string? name, CookieAuthenticationOptions options) =>
            options.Cookie.SecurePolicy = saas.Value.UsesHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

        public void Configure(CookieAuthenticationOptions options) => Configure(Options.DefaultName, options);

        public void Configure(AntiforgeryOptions options) => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    }
}
