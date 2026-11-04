using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Polling;

public static class PollerSetup
{
    /// <summary>
    /// The poller and its parts. The options (<see cref="PollerOptions"/>) are bound by the host. The background loop is
    /// always registered and does nothing when <see cref="PollerOptions.Enabled"/> is false.
    /// </summary>
    public static IServiceCollection AddPoller(this IServiceCollection services)
    {
        services.AddSingleton<RequestBudget>();
        services.AddSingleton<PollerSignal>();
        services.AddSingleton<ServiceEvents>();
        services.AddSingleton<TestMailLimiter>();
        services.AddSingleton<IRequestPacer, RandomPacer>();
        services.AddSingleton<ICinemaCityNetwork>(provider =>
            provider.GetRequiredService<IOptions<PollerOptions>>().Value.Source == CinemaCitySource.Fake
                ? new FakeCinemaCityNetwork(provider.GetRequiredService<IOptions<PollerOptions>>())
                : new RealCinemaCityNetwork());
        services.AddSingleton<PollerCycle>();
        services.AddScoped<CheckService>();
        services.AddHostedService<PollerHostedService>();
        return services;
    }
}
