using Microsoft.EntityFrameworkCore;

namespace CinemaWatch.Server.Data;

public static class DatabaseSetup
{
    /// <summary>Name of the connection string (configuration key ConnectionStrings:CinemaWatch).</summary>
    public const string ConnectionStringName = "CinemaWatch";

    /// <summary>The one place that configures the provider, so the app, the tools and the tests use the same settings.</summary>
    public static DbContextOptionsBuilder UseCinemaWatchDatabase(this DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}
