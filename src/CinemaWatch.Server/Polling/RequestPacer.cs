using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Polling;

/// <summary>Waits before the next screening of a cycle. Tests replace it so they do not wait.</summary>
public interface IRequestPacer
{
    /// <param name="manual">The next screening is checked because of "Check now" (somebody waits for it).</param>
    Task PauseAsync(bool manual, CancellationToken cancellationToken);
}

/// <summary>
/// A random pause: <see cref="PollerOptions.RequestGapMinSeconds"/>-<see cref="PollerOptions.RequestGapMaxSeconds"/>
/// in the background, <see cref="PollerOptions.ManualGapMinSeconds"/>-<see cref="PollerOptions.ManualGapMaxSeconds"/>
/// for "Check now".
/// </summary>
public sealed class RandomPacer(IOptions<PollerOptions> options) : IRequestPacer
{
    public Task PauseAsync(bool manual, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var (min, max) = manual
            ? (settings.ManualGapMinSeconds, settings.ManualGapMaxSeconds)
            : (settings.RequestGapMinSeconds, settings.RequestGapMaxSeconds);
        var seconds = min + Random.Shared.NextDouble() * (max - min);
        return seconds <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }
}
