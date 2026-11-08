using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CinemaWatch.Server.Accounts;

/// <summary>
/// "Forgot your password?" in the background (docs/SAAS.md §9): the page only queues the address and answers at once,
/// the same way and in the same time for every address, so neither the answer nor its timing tells whether an account
/// exists. The queue is bounded and drops new requests when full: a flood of form posts can grow neither memory nor
/// the number of e-mails (<see cref="PasswordHelpLimits"/> still applies to every request that gets through).
/// </summary>
public sealed class PasswordHelpQueue
{
    public const int Capacity = 100;

    private readonly Channel<string> _channel;
    private long _dropped;

    public PasswordHelpQueue() =>
        _channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));

    /// <summary>Requests dropped because the queue was full (since the start).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queues a request; never waits and never fails (a full queue drops it).</summary>
    public void Enqueue(string email) => _channel.Writer.TryWrite(email.Trim());

    internal IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Works through <see cref="PasswordHelpQueue"/>: one request at a time, each in its own scope.</summary>
public sealed class PasswordHelpSender(PasswordHelpQueue queue, IServiceScopeFactory scopes, ILogger<PasswordHelpSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var email in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AccountService>().RequestPasswordHelpAsync(email, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The person saw the same answer as for success; the operator sees the mail failure in the panel.
                    logger.LogError(ex, "Password help e-mail could not be sent");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
