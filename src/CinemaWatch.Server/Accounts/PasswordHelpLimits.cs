namespace CinemaWatch.Server.Accounts;

public enum PasswordHelpDecision
{
    /// <summary>The e-mail may be sent; it has been counted.</summary>
    Allowed,

    /// <summary>This account got a password help e-mail less than <see cref="PasswordHelpLimits.PerAccountInterval"/> ago.</summary>
    TooSoonForAccount,

    /// <summary><see cref="PasswordHelpLimits.PerDay"/> password help e-mails went out in the last 24 hours.</summary>
    DailyLimit,
}

/// <summary>
/// Limits on the password help e-mails ("Forgot your password?", docs/SAAS.md §9). Anybody can ask for them without
/// signing in, and the free e-mail plan stops sending at 100 e-mails a day: without limits a stranger could use up the
/// day's e-mails and no seat notification would go out. One e-mail per account per 15 minutes, at most 20 a day for
/// all accounts together. Kept in memory: a restart starts afresh, which is fine for one server.
/// </summary>
public sealed class PasswordHelpLimits(TimeProvider time)
{
    public static readonly TimeSpan PerAccountInterval = TimeSpan.FromMinutes(15);

    public const int PerDay = 20;

    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastByAccount = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _sent = new();

    /// <summary>Counts an e-mail for the account if both limits allow it. Failed e-mails count too.</summary>
    public PasswordHelpDecision TryReserve(string userId)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            while (_sent.Count > 0 && now - _sent.Peek() >= Day)
                _sent.Dequeue();

            if (_lastByAccount.TryGetValue(userId, out var last) && now - last < PerAccountInterval)
                return PasswordHelpDecision.TooSoonForAccount;
            if (_sent.Count >= PerDay)
                return PasswordHelpDecision.DailyLimit;

            _lastByAccount[userId] = now;
            _sent.Enqueue(now);
            return PasswordHelpDecision.Allowed;
        }
    }

    /// <summary>A deleted account's entry is dropped (it would otherwise stay until the next restart).</summary>
    public void Forget(string userId)
    {
        lock (_gate)
            _lastByAccount.Remove(userId);
    }
}
