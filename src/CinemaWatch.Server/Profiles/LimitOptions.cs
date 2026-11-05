namespace CinemaWatch.Server.Profiles;

/// <summary>
/// Limits per account (configuration section "Saas:Limits"). They protect the server's disk and the Cinema City
/// request budget (docs/SAAS.md §4.3, §5.1). The owner can give one account its own profile limit
/// (<see cref="Data.AppUser.MaxProfiles"/>); later a paid plan sets it (entitlements, docs/SAAS.md §4.5).
/// </summary>
public sealed class LimitOptions
{
    public const string Section = "Saas:Limits";

    /// <summary>Highest profile limit the owner can give an account: a typo such as 5000 must not get through.</summary>
    public const int ProfileLimitCeiling = 50;

    /// <summary>The server minimum cannot be configured below this (KB §3.6: 10-15 minutes recommended, 5 allowed).</summary>
    public const int MinIntervalFloor = 5;

    /// <summary>Profiles per account (enabled, disabled and ended alike) unless the account has its own limit.</summary>
    public int MaxProfiles { get; set; } = 5;

    /// <summary>Watched screenings in one profile: every one costs requests at every check.</summary>
    public int MaxScreeningsPerProfile { get; set; } = 20;

    /// <summary>
    /// Checks never run more often than this, whatever interval the profile asks for. The operator's panel can change
    /// it within <see cref="Polling.PollerTuning"/>'s range; the effective value comes from <see cref="Polling.PollerSettings"/>.
    /// </summary>
    public int MinIntervalMinutes { get; set; } = 10;

    public bool IsValid() =>
        MaxProfiles is >= 1 and <= ProfileLimitCeiling
        && MaxScreeningsPerProfile >= 1
        && MinIntervalMinutes >= MinIntervalFloor;

    public LimitOptions Copy() => (LimitOptions)MemberwiseClone();

    /// <summary>The interval the server actually uses for a profile.</summary>
    public int EffectiveInterval(int profileInterval) => Math.Max(profileInterval, MinIntervalMinutes);
}
