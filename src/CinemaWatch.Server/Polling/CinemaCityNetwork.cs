using CinemaWatch.Core;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Polling;

/// <summary>Creates the network handler for one poller cycle (a fresh session per cycle, like one probe run).</summary>
public interface ICinemaCityNetwork
{
    HttpMessageHandler CreateHandler();
}

/// <summary>The real Cinema City API: the desktop app's network settings (decompression, cookies).</summary>
public sealed class RealCinemaCityNetwork : ICinemaCityNetwork
{
    public HttpMessageHandler CreateHandler() => CinemaCityClient.CreateDefaultHandler();
}

/// <summary>The development fake (<see cref="FakeCinemaCityHandler"/>) steered by files in a folder.</summary>
public sealed class FakeCinemaCityNetwork(IOptions<PollerOptions> options) : ICinemaCityNetwork
{
    public HttpMessageHandler CreateHandler() => new FakeCinemaCityHandler(options.Value.FakeDirectory);
}
