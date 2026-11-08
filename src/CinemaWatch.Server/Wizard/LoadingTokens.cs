using CinemaWatch.Server.Polling;

namespace CinemaWatch.Server.Wizard;

/// <summary>Waits before the next item of a wizard loading. Tests replace it so they do not wait.</summary>
public interface IWizardPacer : IRequestPacer;

/// <summary>A fixed pause of <see cref="WizardOptions.GapSeconds"/> (the effective value: the panel can change it).</summary>
public sealed class WizardPacer(PollerSettings current) : IWizardPacer
{
    public Task PauseAsync(bool manual, CancellationToken cancellationToken)
    {
        var gap = current.Poller.Wizard.Gap;
        return gap <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(gap, cancellationToken);
    }
}

/// <summary>
/// The tokens of one wizard loading. At the start the loading reserves what it is expected to need from the wizard's
/// budget and from the account's limit together, so a loading that started also finishes, whatever other loadings do
/// meanwhile. A request beyond the reservation (the estimate was too low) takes from both buckets directly; what is
/// left of the reservation at the end goes back to both (answers that came from the cache after all).
/// </summary>
public sealed class LoadingTokens(RequestBudget wizard, RequestBudget account) : IRequestTokens
{
    private readonly Lock _gate = new();
    private int _reserved;

    /// <summary>Which bucket was empty when a request beyond the reservation was refused.</summary>
    public WizardStopReason? Shortage { get; private set; }

    /// <summary>Reserves <paramref name="count"/> tokens from both buckets, or none when either has too few.</summary>
    public bool TryReserve(int count)
    {
        lock (_gate)
        {
            if (count <= 0)
                return true;
            if (account.Available < count || !wizard.TryTake(count))
                return false;
            if (!account.TryTake(count))
            {
                wizard.Return(count);
                return false;
            }
            _reserved += count;
            return true;
        }
    }

    public bool TryTake()
    {
        lock (_gate)
        {
            if (_reserved > 0)
            {
                _reserved--;
                return true;
            }
            if (account.Available < 1)
            {
                Shortage = WizardStopReason.AccountLimit;
                return false;
            }
            if (!wizard.TryTake())
            {
                Shortage = WizardStopReason.BudgetUsedUp;
                return false;
            }
            if (!account.TryTake())
            {
                wizard.Return(1);
                Shortage = WizardStopReason.AccountLimit;
                return false;
            }
            return true;
        }
    }

    /// <summary>Gives the unused part of the reservation back to both buckets.</summary>
    public void Release()
    {
        lock (_gate)
        {
            wizard.Return(_reserved);
            account.Return(_reserved);
            _reserved = 0;
        }
    }
}
