namespace CinemaWatch.Server.Mail;

public enum MailSecurity
{
    /// <summary>Plain connection - only for a local test inbox such as smtp4dev.</summary>
    None,

    /// <summary>Connect in plain text, then upgrade to TLS (usually port 587).</summary>
    StartTls,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    SslOnConnect,
}

/// <summary>SMTP settings (configuration section "Mail"). A password only from user secrets or environment variables.</summary>
public sealed class MailOptions
{
    public const string Section = "Mail";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 2525;
    public MailSecurity Security { get; set; } = MailSecurity.None;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "cinemawatch@localhost";
    public string FromName { get; set; } = "CinemaWatch";
}
