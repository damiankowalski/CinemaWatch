using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using Microsoft.EntityFrameworkCore;

namespace CinemaWatch.Web;

internal static class Startup
{
    /// <summary>
    /// Refuses to start on a database without the current migrations (development: dotnet ef database update), loads the
    /// poller settings changed in the operator's panel, then creates and invites the owner's account if it does not
    /// exist yet.
    /// </summary>
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
            throw new InvalidOperationException(
                $"The database is missing {pending.Count} migration(s): {string.Join(", ", pending)}. " +
                "Development: dotnet ef database update (src/CinemaWatch.Web/README.md).");

        await scope.ServiceProvider.GetRequiredService<PollerSettings>().RefreshAsync(db);
        await scope.ServiceProvider.GetRequiredService<AccountService>().EnsureOwnerAsync();
    }
}
