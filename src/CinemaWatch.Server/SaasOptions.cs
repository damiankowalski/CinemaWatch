namespace CinemaWatch.Server;

/// <summary>Settings of the SaaS (configuration section "Saas").</summary>
public sealed class SaasOptions
{
    public const string Section = "Saas";

    /// <summary>
    /// Public address of the site, e.g. http://localhost:5180 or https://cinemawatch.damiankowalski.pl. Links in
    /// e-mails are built from it, never from the request's Host header (which a client can forge). An https address
    /// also makes the sign-in and form cookies Secure, whatever the request looked like (<see cref="UsesHttps"/>).
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// E-mail of the owner: the account is created and invited at start-up, and only it may invite others.
    /// Personal data: set it in user secrets or an environment variable, never in a file in the repository.
    /// </summary>
    public string OwnerEmail { get; set; } = "";

    /// <summary>
    /// The app runs behind a reverse proxy (on the server: <c>cloudflared</c>, the Cloudflare Tunnel, in the same
    /// container network): its <c>X-Forwarded-Proto</c> and <c>X-Forwarded-For</c> headers are trusted, so the app sees
    /// the visitor's HTTPS instead of the tunnel's plain HTTP. Only where nothing but the proxy can reach the app (the
    /// containers publish no ports, docs/SAAS.md §4.4); off on the development PC and in the tests.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Apply missing database migrations at start-up (the container on the server: one instance, a <c>pg_dump</c> before
    /// every deployment). Off = refuse to start with missing migrations (development: <c>dotnet ef database update</c>).
    /// </summary>
    public bool MigrateDatabaseOnStart { get; set; }

    /// <summary>Whether <see cref="BaseUrl"/> is an https address: the public site is reached over HTTPS only.</summary>
    public bool UsesHttps =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
