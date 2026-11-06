using Microsoft.EntityFrameworkCore;

namespace CinemaWatch.Server.Data;

public static class DatabaseSetup
{
    /// <summary>Name of the connection string (configuration key ConnectionStrings:CinemaWatch).</summary>
    public const string ConnectionStringName = "CinemaWatch";

    /// <summary>The one place that configures the provider, so the app, the tools and the tests use the same settings.</summary>
    public static DbContextOptionsBuilder UseCinemaWatchDatabase(this DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    /// <summary>
    /// Makes sure the database has every migration of this version. With <paramref name="applyMissing"/> the missing ones
    /// are applied (an empty or missing database gets them all) and returned; without it a database with missing
    /// migrations is refused, so the app never runs on a schema it does not know.
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnsureMigratedAsync(
        this AppDbContext db, bool applyMissing, CancellationToken cancellationToken = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
            return [];
        if (!applyMissing)
            throw new InvalidOperationException(
                $"The database is missing {pending.Count} migration(s): {string.Join(", ", pending)}. " +
                "Development: dotnet ef database update (src/CinemaWatch.Web/README.md); " +
                "on the server: Saas:MigrateDatabaseOnStart=true applies them at start-up.");
        await db.Database.MigrateAsync(cancellationToken);
        return pending;
    }
}
