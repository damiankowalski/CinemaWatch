namespace CinemaWatch.Server;

/// <summary>Settings of the SaaS (configuration section "Saas").</summary>
public sealed class SaasOptions
{
    public const string Section = "Saas";

    /// <summary>
    /// Public address of the site, e.g. http://localhost:5180 or https://cinemawatch.damiankowalski.pl. Links in
    /// e-mails are built from it, never from the request's Host header (which a client can forge).
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// E-mail of the owner: the account is created and invited at start-up, and only it may invite others.
    /// Personal data: set it in user secrets or an environment variable, never in a file in the repository.
    /// </summary>
    public string OwnerEmail { get; set; } = "";
}
