using CinemaWatch.Server.Data;
using CinemaWatch.Server.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CinemaWatch.Server.Mail;

/// <summary>
/// Sends through the real transport (SMTP; the tests' recorder) and records the outcome of every e-mail - invitations,
/// test e-mails, notifications, alerts - as the "mail" row of component_status, for the operator's panel. Recording
/// never changes the outcome: a failed recording is logged, the e-mail's own error is thrown as before. Addresses are
/// cut out of the recorded error (<see cref="MailErrors"/>).
/// </summary>
public sealed class StatusRecordingMailSender(
    IMailSender transport,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<StatusRecordingMailSender> logger) : IMailSender
{
    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        try
        {
            await transport.SendAsync(mail, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordAsync(MailErrors.WithoutAddresses(ex.Message)).ConfigureAwait(false);
            throw;
        }
        await RecordAsync(null).ConfigureAwait(false);
    }

    private async Task RecordAsync(string? failure)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ComponentStatuses.RecordAsync(db, ComponentStatusRow.Mail, time.GetUtcNow(), failure).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The e-mail's outcome could not be recorded for the operator's panel");
        }
    }
}

public static class MailSetup
{
    /// <summary>
    /// The mail sender everybody uses: <paramref name="transport"/> wrapped so that every outcome is recorded. Called
    /// again with another transport, the last call wins (the tests replace SMTP with a recorder this way).
    /// </summary>
    public static IServiceCollection AddMailSender(this IServiceCollection services, Func<IServiceProvider, IMailSender> transport) =>
        services.AddSingleton<IMailSender>(provider => ActivatorUtilities.CreateInstance<StatusRecordingMailSender>(provider, transport(provider)));
}

/// <summary>
/// Error texts of mail servers often quote the recipient ("5.1.1 &lt;anna@example.com&gt;: Recipient rejected"). Texts kept
/// for the operator (service events, component status) lose the addresses, so they hold no personal data.
/// </summary>
public static partial class MailErrors
{
    public const string Placeholder = "[e-mail]";

    public static string WithoutAddresses(string text) => Address().Replace(text, Placeholder);

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\s<>""'(),;:\[\]]+@[^\s<>""'(),;:\[\]]+")]
    private static partial System.Text.RegularExpressions.Regex Address();
}
