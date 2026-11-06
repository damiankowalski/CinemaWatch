using System.Globalization;
using CinemaWatch.Core;
using CinemaWatch.Server;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Web;

internal static class Startup
{
    /// <summary>
    /// Checks the platform (time-zone data and ICU), then the database schema - missing migrations are applied when <c>Saas:MigrateDatabaseOnStart</c> is on (the
    /// container on the server), otherwise the start is refused (development: dotnet ef database update) - then loads the
    /// poller settings changed in the operator's panel and creates and invites the owner's account if it does not exist
    /// yet.
    /// </summary>
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        CheckPlatform();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SaasOptions>>().Value;
        var applied = await db.EnsureMigratedAsync(options.MigrateDatabaseOnStart);
        if (applied.Count > 0)
            app.Logger.LogInformation("Applied {Count} database migration(s): {Migrations}", applied.Count, string.Join(", ", applied));

        await scope.ServiceProvider.GetRequiredService<PollerSettings>().RefreshAsync(db);
        await scope.ServiceProvider.GetRequiredService<AccountService>().EnsureOwnerAsync();
    }

    /// <summary>
    /// Screening times are Polish time and pages and e-mails show Polish dates, so the system (the container image on
    /// the server) must carry the time-zone data and ICU. Without them the app would fail later, at the first check, or
    /// show dates in the wrong language; it refuses to start instead.
    /// </summary>
    private static void CheckPlatform()
    {
        try
        {
            _ = PolishTime.Zone;
        }
        catch (TypeInitializationException ex) when (ex.InnerException is TimeZoneNotFoundException)
        {
            throw new InvalidOperationException("The time zone Europe/Warsaw is missing: the system needs tzdata (container: a *-chiseled-extra image).", ex);
        }
        if (CultureInfo.GetCultureInfo("pl-PL").DateTimeFormat.GetDayName(DayOfWeek.Wednesday) != "środa")
            throw new InvalidOperationException("Polish culture data is missing: the system needs ICU and must not run in invariant globalization mode (container: a *-chiseled-extra image).");
    }
}
