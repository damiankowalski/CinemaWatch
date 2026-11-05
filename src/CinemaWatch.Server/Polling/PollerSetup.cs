using CinemaWatch.Server.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Polling;

public static class PollerSetup
{
    /// <summary>
    /// The poller and its parts, the heartbeat and the operator's panel. The options (<see cref="PollerOptions"/>,
    /// <see cref="Profiles.LimitOptions"/>, <see cref="HeartbeatOptions"/>) are bound by the host. The background loop is
    /// always registered and does nothing when <see cref="PollerOptions.Enabled"/> is false.
    /// </summary>
    public static IServiceCollection AddPoller(this IServiceCollection services)
    {
        services.AddSingleton<PollerSettings>();
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
        services.AddSingleton<IHeartbeatClient, HttpHeartbeatClient>();
        services.AddSingleton<Heartbeat>();
        services.AddScoped<OperatorService>();
        services.AddHostedService<PollerHostedService>();
        return services;
    }
}
