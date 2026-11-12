using System.Text;
using CinemaWatch.Core;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Profiles;

/// <summary>One uploaded file. <see cref="Length"/> is what the browser declared; reading stops after the size limit anyway.</summary>
public sealed record ProfileUpload(string FileName, long Length, Stream Content);

public enum ImportOutcome
{
    /// <summary>A new profile was stored (enabled).</summary>
    Added,

    /// <summary>A profile with the same name existed and was replaced; its id and enabled state stay.</summary>
    Replaced,

    /// <summary>The file was refused (see the errors).</summary>
    Invalid,

    /// <summary>The file is fine, but the account already has as many profiles as its limit allows.</summary>
    LimitReached,
}

public sealed record ImportResult(string FileName, string ProfileName, ImportOutcome Outcome, IReadOnlyList<string> Errors);

/// <summary>A stored profile for the profile list, with its document read back.</summary>
public sealed record ProfileView(Guid Id, string Name, bool Enabled, bool Ended, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, WatchProfile Document);

public sealed record ProfileList(IReadOnlyList<ProfileView> Profiles, int Limit);

public sealed record ProfileDownload(string FileName, string Json);

public enum SaveOutcome
{
    /// <summary>A new profile was stored (enabled).</summary>
    Added,

    /// <summary>The edited profile was changed; its id and enabled state stay.</summary>
    Updated,

    /// <summary>Another profile of the account has this name (the wizard never replaces one by its name).</summary>
    NameTaken,

    /// <summary>A new profile, but the account already has as many profiles as its limit allows.</summary>
    LimitReached,

    /// <summary>The edited profile is gone (deleted meanwhile, or not the account's).</summary>
    NotFound,

    /// <summary>The import's checks refused the profile (see the errors).</summary>
    Invalid,
}

/// <summary>What saving a profile from the wizard did; <see cref="Id"/> is the stored profile's id when it was saved.</summary>
public sealed record SaveResult(SaveOutcome Outcome, Guid? Id, string Name, IReadOnlyList<string> Errors);

public enum LimitChange
{
    Saved,
    NotFound,
    OutOfRange,
}

/// <summary>
/// Profiles of one account (docs/SAAS.md §5.1). Every operation takes the account's id and touches only that
/// account's rows: someone else's profile id behaves like an id that does not exist. Makes no Cinema City requests.
/// </summary>
public sealed class ProfileService(AppDbContext db, PollerSettings settings, TimeProvider time, ILogger<ProfileService> logger)
{
    /// <summary>The account's profile limit: its own one if the owner set it, otherwise the default.</summary>
    public int LimitOf(AppUser user) => user.MaxProfiles ?? settings.Limits.MaxProfiles;

    /// <summary>The effective limits: the minimum interval may come from the operator's panel.</summary>
    public LimitOptions Limits => settings.Limits;

    public async Task<ProfileList> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return new ProfileList([], settings.Limits.MaxProfiles);

        var rows = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        var views = rows.Select(p => new ProfileView(p.Id, p.Name, p.Enabled, p.EndsAt <= now, p.CreatedAt, p.UpdatedAt, ProfileStore.Parse(p.Document))).ToList();
        return new ProfileList(views, LimitOf(user));
    }

    /// <summary>
    /// Checks and stores the files in order. A profile with the name of an existing one replaces it (its id and enabled
    /// state stay); a new one is stored enabled while the account is under its limit, otherwise refused.
    /// </summary>
    public async Task<IReadOnlyList<ImportResult>> ImportAsync(string userId, IReadOnlyList<ProfileUpload> files, CancellationToken cancellationToken = default)
    {
        var polishNow = PolishTime.Now(time);
        var checks = new List<(ProfileUpload File, ProfileCheck Check)>();
        foreach (var file in files)
            checks.Add((file, await CheckAsync(file, polishNow, cancellationToken)));

        var results = new List<ImportResult>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Lock the account's row: two imports at once (two browser tabs) cannot both take the last free place.
        var user = (await db.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {userId} FOR UPDATE").ToListAsync(cancellationToken)).SingleOrDefault()
            ?? throw new InvalidOperationException("The account does not exist.");
        var stored = await db.Profiles.Where(p => p.UserId == userId).ToListAsync(cancellationToken);
        var limit = LimitOf(user);
        var now = time.GetUtcNow();

        foreach (var (file, check) in checks)
        {
            if (!check.IsValid)
            {
                results.Add(new ImportResult(file.FileName, check.Name, ImportOutcome.Invalid, check.Errors));
                continue;
            }

            var document = ProfileStore.Serialize(check.Profile!);
            var endsAt = ProfileImport.LastStartUtc(check.Profile!);
            var existing = stored.FirstOrDefault(p => p.Name == check.Name);
            if (existing is not null)
            {
                existing.Document = document;
                existing.EndsAt = endsAt;
                existing.UpdatedAt = now;
                results.Add(new ImportResult(file.FileName, check.Name, ImportOutcome.Replaced, []));
            }
            else if (stored.Count >= limit)
            {
                results.Add(new ImportResult(file.FileName, check.Name, ImportOutcome.LimitReached, []));
            }
            else
            {
                var profile = new StoredProfile
                {
                    Id = Guid.CreateVersion7(now),
                    UserId = userId,
                    Name = check.Name,
                    Enabled = true,
                    Document = document,
                    EndsAt = endsAt,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Profiles.Add(profile);
                stored.Add(profile);
                results.Add(new ImportResult(file.FileName, check.Name, ImportOutcome.Added, []));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("User {UserId} imported {Files} file(s): {Added} added, {Replaced} replaced, {Invalid} refused, {Limit} over the limit",
            userId, results.Count, Count(ImportOutcome.Added), Count(ImportOutcome.Replaced), Count(ImportOutcome.Invalid), Count(ImportOutcome.LimitReached));
        return results;

        int Count(ImportOutcome outcome) => results.Count(r => r.Outcome == outcome);
    }

    /// <summary>One of the account's profiles with its document (the wizard's editing), or null.</summary>
    public async Task<ProfileView?> OpenAsync(string userId, Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == profileId && p.UserId == userId, cancellationToken);
        return profile is null
            ? null
            : new ProfileView(profile.Id, profile.Name, profile.Enabled, profile.EndsAt <= time.GetUtcNow(), profile.CreatedAt, profile.UpdatedAt, ProfileStore.Parse(profile.Document));
    }

    /// <summary>
    /// Saves a profile made in the site's wizard: the import's checks and limits (the document goes through
    /// <see cref="ProfileImport.Check"/> exactly as an uploaded file), the account's row locked like an import. A new
    /// profile (<paramref name="editing"/> null) is stored enabled; an edited one keeps its id and enabled state, and
    /// its notification state stays for the screenings and seats it still watches (seats already reported free are not
    /// reported again; seats added back later are). A name another profile of the account has is refused.
    /// </summary>
    public async Task<SaveResult> SaveAsync(string userId, WatchProfile profile, Guid? editing, CancellationToken cancellationToken = default)
    {
        var check = ProfileImport.Check(ProfileImport.FileNameFor(profile.Name), ProfileStore.Serialize(profile),
            PolishTime.Now(time), settings.Limits.MaxScreeningsPerProfile);
        if (!check.IsValid)
            return new SaveResult(SaveOutcome.Invalid, null, check.Name, check.Errors);
        var checkedProfile = check.Profile!;
        var name = check.Name;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = (await db.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {userId} FOR UPDATE").ToListAsync(cancellationToken)).SingleOrDefault()
            ?? throw new InvalidOperationException("The account does not exist.");
        var stored = await db.Profiles.Where(p => p.UserId == userId).ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (stored.Any(p => p.Name == name && p.Id != editing))
            return new SaveResult(SaveOutcome.NameTaken, null, name, []);

        StoredProfile target;
        SaveOutcome outcome;
        if (editing is { } id)
        {
            if (stored.FirstOrDefault(p => p.Id == id) is not { } edited)
                return new SaveResult(SaveOutcome.NotFound, null, name, []);
            target = edited;
            target.Name = name;
            outcome = SaveOutcome.Updated;

            var watched = checkedProfile.Screenings.ToDictionary(s => s.Id, s => s.SeatKeys.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var state in await db.NotificationStates.Where(s => s.ProfileId == id).ToListAsync(cancellationToken))
            {
                if (!watched.TryGetValue(state.ScreeningId, out var seats))
                {
                    db.NotificationStates.Remove(state);
                    continue;
                }
                var kept = state.AvailableSeatKeys.Where(seats.Contains).ToList();
                if (kept.Count != state.AvailableSeatKeys.Count)
                {
                    state.AvailableSeatKeys = kept;
                    state.UpdatedAt = now;
                }
            }
        }
        else
        {
            if (stored.Count >= LimitOf(user))
                return new SaveResult(SaveOutcome.LimitReached, null, name, []);
            target = new StoredProfile
            {
                Id = Guid.CreateVersion7(now),
                UserId = userId,
                Name = name,
                Enabled = true,
                CreatedAt = now,
            };
            db.Profiles.Add(target);
            outcome = SaveOutcome.Added;
        }
        target.Document = ProfileStore.Serialize(checkedProfile);
        target.EndsAt = ProfileImport.LastStartUtc(checkedProfile);
        target.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("User {UserId} saved profile {ProfileId} from the wizard ({Outcome}, {Screenings} screening(s))",
            userId, target.Id, outcome, checkedProfile.Screenings.Count);
        return new SaveResult(outcome, target.Id, name, []);
    }

    /// <summary>Enables or disables a profile; the name for the message, or null when the account has no such profile.</summary>
    public async Task<string?> SetEnabledAsync(string userId, Guid profileId, bool enabled, CancellationToken cancellationToken = default)
    {
        var profile = await FindAsync(userId, profileId, cancellationToken);
        if (profile is null)
            return null;
        if (profile.Enabled != enabled)
        {
            profile.Enabled = enabled;
            profile.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        return profile.Name;
    }

    /// <summary>Deletes a profile; its name, or null when the account has no such profile.</summary>
    public async Task<string?> DeleteAsync(string userId, Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await FindAsync(userId, profileId, cancellationToken);
        if (profile is null)
            return null;
        db.Profiles.Remove(profile);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} deleted profile {ProfileId}", userId, profileId);
        return profile.Name;
    }

    /// <summary>The stored profile as the desktop app would save it, with the desktop's file name.</summary>
    public async Task<ProfileDownload?> DownloadAsync(string userId, Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == profileId && p.UserId == userId, cancellationToken);
        return profile is null
            ? null
            : new ProfileDownload(ProfileImport.FileNameFor(profile.Name), ProfileStore.Serialize(ProfileStore.Parse(profile.Document)));
    }

    /// <summary>Number of profiles per account id (owner's account list).</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountByAccountAsync(CancellationToken cancellationToken = default) =>
        await db.Profiles.GroupBy(p => p.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

    /// <summary>
    /// Owner: gives an account its own profile limit (1 to <see cref="LimitOptions.ProfileLimitCeiling"/>), or the
    /// default again with null. A limit below the current number of profiles deletes nothing; it only stops new imports.
    /// </summary>
    public async Task<LimitChange> SetLimitAsync(string userId, int? limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > LimitOptions.ProfileLimitCeiling)
            return LimitChange.OutOfRange;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return LimitChange.NotFound;
        user.MaxProfiles = limit;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Profile limit of user {UserId} set to {Limit}", userId, limit?.ToString() ?? "the default");
        return LimitChange.Saved;
    }

    private Task<StoredProfile?> FindAsync(string userId, Guid profileId, CancellationToken cancellationToken) =>
        db.Profiles.SingleOrDefaultAsync(p => p.Id == profileId && p.UserId == userId, cancellationToken);

    private async Task<ProfileCheck> CheckAsync(ProfileUpload file, DateTime polishNow, CancellationToken cancellationToken)
    {
        var fileName = file.FileName;
        if (file.Length > ProfileImport.MaxFileBytes)
            return TooLarge();

        // Never trust the declared length: read at most one byte more than allowed.
        var buffer = new byte[ProfileImport.MaxFileBytes + 1];
        var read = 0;
        int chunk;
        while (read < buffer.Length && (chunk = await file.Content.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
            read += chunk;
        if (read > ProfileImport.MaxFileBytes)
            return TooLarge();

        // UTF-8 (the desktop app writes it without a byte order mark; one is tolerated).
        var json = new UTF8Encoding(false).GetString(buffer, 0, read).TrimStart('\uFEFF');
        return ProfileImport.Check(fileName, json, polishNow, settings.Limits.MaxScreeningsPerProfile);

        ProfileCheck TooLarge() => ProfileImport.Refuse(fileName, Localizer.T(
            $"Plik jest za duży (najwyżej {ProfileImport.MaxFileBytes / 1024} KB).",
            $"The file is too large (at most {ProfileImport.MaxFileBytes / 1024} KB)."));
    }
}
