using CinemaWatch.Server.Profiles;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CinemaWatch.Server.Data;

/// <summary>
/// The SaaS database (PostgreSQL). One database for everybody; every row of user data has an owner
/// (docs/SAAS.md §4.2). Names are snake_case (users, user_tokens, data_protection_keys, ...), so they need no
/// quotes in psql. Identity without roles: the owner is recognised by the e-mail in the settings.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityUserContext<AppUser>(options), IDataProtectionKeyContext
{
    /// <summary>Keys that protect sign-in cookies and e-mail links; kept in the database so they survive restarts and redeployments.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    public DbSet<StoredProfile> Profiles { get; set; } = null!;

    public DbSet<WatchedScreeningRow> WatchedScreenings { get; set; } = null!;

    public DbSet<SeatPlanRow> SeatPlans { get; set; } = null!;

    public DbSet<NotificationStateRow> NotificationStates { get; set; } = null!;

    public DbSet<NotificationRecord> Notifications { get; set; } = null!;

    public DbSet<CheckLogEntry> CheckLog { get; set; } = null!;

    public DbSet<PollerStateRow> PollerState { get; set; } = null!;

    public DbSet<ServiceEvent> ServiceEvents { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<AppUser>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.Language).HasMaxLength(2);
            user.Ignore(u => u.IsPending);
        });
        builder.Entity<StoredProfile>(profile =>
        {
            profile.ToTable("profiles");
            profile.Property(p => p.Name).HasMaxLength(ProfileImport.MaxTextLength);
            profile.Property(p => p.Document).HasColumnType("jsonb");
            // Deleting an account deletes its profiles (in the database, not only in EF).
            profile.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            profile.HasIndex(p => new { p.UserId, p.Name }).IsUnique();
        });

        // Poller (docs/SAAS.md §4.2, §4.3).
        builder.Entity<WatchedScreeningRow>(row =>
        {
            row.ToTable("watched_screenings");
            row.Property(r => r.Id).HasMaxLength(ProfileImport.MaxIdLength);
            row.Property(r => r.CinemaId).HasMaxLength(ProfileImport.MaxIdLength);
            row.Property(r => r.LastOutcome).HasConversion<string>().HasMaxLength(16);
            row.Property(r => r.LastError).HasMaxLength(500);
            row.HasIndex(r => r.NextCheckAt);
        });
        builder.Entity<SeatPlanRow>(plan =>
        {
            plan.ToTable("seat_plans");
            plan.HasKey(p => new { p.VenueId, p.SeatplanId });
        });
        builder.Entity<NotificationStateRow>(state =>
        {
            state.ToTable("notification_state");
            state.HasKey(s => new { s.ProfileId, s.ScreeningId });
            state.Property(s => s.ScreeningId).HasMaxLength(ProfileImport.MaxIdLength);
            state.HasOne<StoredProfile>().WithMany().HasForeignKey(s => s.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<NotificationRecord>(record =>
        {
            record.ToTable("notifications");
            record.Property(n => n.Subject).HasMaxLength(300);
            record.Property(n => n.Error).HasMaxLength(500);
            record.HasOne<StoredProfile>().WithMany().HasForeignKey(n => n.ProfileId).OnDelete(DeleteBehavior.Cascade);
            record.HasIndex(n => new { n.ProfileId, n.CreatedAt });
            record.HasIndex(n => n.CreatedAt);
        });
        builder.Entity<CheckLogEntry>(entry =>
        {
            entry.ToTable("check_log");
            entry.Property(e => e.Cycle).HasMaxLength(32);
            entry.Property(e => e.ScreeningId).HasMaxLength(ProfileImport.MaxIdLength);
            entry.Property(e => e.Method).HasMaxLength(8);
            entry.Property(e => e.Host).HasMaxLength(100);
            entry.Property(e => e.Endpoint).HasMaxLength(100);
            entry.Property(e => e.Target).HasMaxLength(500);
            entry.Property(e => e.ContentType).HasMaxLength(100);
            entry.Property(e => e.CfMitigated).HasMaxLength(100);
            entry.Property(e => e.CfRay).HasMaxLength(100);
            entry.Property(e => e.RetryAfter).HasMaxLength(100);
            entry.Property(e => e.Error).HasMaxLength(500);
            entry.HasIndex(e => e.At);
        });
        builder.Entity<PollerStateRow>(state =>
        {
            state.ToTable("poller_state");
            state.Property(s => s.Id).ValueGeneratedNever();
            state.Property(s => s.BlockReason).HasMaxLength(500);
        });
        builder.Entity<ServiceEvent>(serviceEvent =>
        {
            serviceEvent.ToTable("service_events");
            serviceEvent.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32);
            serviceEvent.Property(e => e.Message).HasMaxLength(1000);
            serviceEvent.HasIndex(e => e.At);
        });
        builder.Entity<IdentityUserClaim<string>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<string>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<string>>().ToTable("user_tokens");
    }
}
