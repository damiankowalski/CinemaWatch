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
        builder.Entity<IdentityUserClaim<string>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<string>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<string>>().ToTable("user_tokens");
    }
}
