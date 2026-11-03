namespace CinemaWatch.Server.Data;

/// <summary>
/// A profile imported by an account (table profiles). <see cref="Document"/> is the profile in format version 1
/// (docs/KNOWLEDGE_BASE.md §5), normalised the way the desktop app saves it, so a download opens in the desktop app.
/// Within one account the name identifies the profile: importing a profile with the same name replaces it.
/// </summary>
public sealed class StoredProfile
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = "";

    public AppUser? User { get; set; }

    public string Name { get; set; } = "";

    public bool Enabled { get; set; }

    /// <summary>The profile JSON (jsonb). Read it with <see cref="Core.ProfileStore.Parse"/>.</summary>
    public string Document { get; set; } = "";

    /// <summary>Start of the last screening (UTC). After it the profile has ended; the poller skips it.</summary>
    public DateTimeOffset EndsAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
