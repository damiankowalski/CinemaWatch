using CinemaWatch.Server.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Polling;

/// <summary>Wakes the poller early ("Check now"); otherwise it runs every <see cref="PollerOptions.TickSeconds"/>.</summary>
public sealed class PollerSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _wake.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Runs <see cref="PollerCycle"/> in the background of the web app (MVP 0: one process; a worker host later). A failing
/// cycle is logged and recorded, never ends the loop; the next tick tries again.
/// </summary>
public sealed class PollerHostedService(
    PollerCycle cycle,
    PollerSignal signal,
    IOptions<PollerOptions> options,
    IServiceScopeFactory scopes,
    ServiceEvents events,
    ILogger<PollerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Poller disabled (Saas:Poller:Enabled = false): no background checks");
            return;
        }
        await Task.Yield();

        if (settings.Source == CinemaCitySource.Fake)
        {
            FakeCinemaCityHandler.PrepareFolder(settings.FakeDirectory);
            logger.LogWarning("Poller uses the development FAKE of Cinema City, steered by files in {Folder}", settings.FakeDirectory);
        }
        var description = $"Poller {PollerInfo.Version} started: source {settings.Source}, budget {settings.RequestsPerHour} requests/h, " +
                          $"{settings.RequestGapMinSeconds:0.#}-{settings.RequestGapMaxSeconds:0.#} s between screenings " +
                          $"({settings.ManualGapMinSeconds:0.#}-{settings.ManualGapMaxSeconds:0.#} s for \"check now\")";
        logger.LogInformation("{Description}", description);
        await RecordAsync(db => events.Add(db, ServiceEventKind.PollerStarted, description), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cycle.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Poller cycle failed; the next one follows in {Seconds} s", settings.TickSeconds);
                await RecordAsync(db => events.Add(db, ServiceEventKind.CycleFailed, $"{ex.GetType().Name}: {ex.Message}", ex.ToString(), throttled: true), stoppingToken);
            }

            try
            {
                await signal.WaitAsync(settings.Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RecordAsync(Action<AppDbContext> add, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            add(db);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "A service event could not be saved");
        }
    }
}
