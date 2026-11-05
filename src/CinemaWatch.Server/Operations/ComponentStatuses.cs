using CinemaWatch.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace CinemaWatch.Server.Operations;

/// <summary>
/// Writes the last success or failure of a component (table component_status) in one statement: an insert that
/// becomes an update when the row exists, so two e-mails sent at the same moment cannot collide.
/// </summary>
public static class ComponentStatuses
{
    public const int MaxFailureLength = 500;

    public static async Task RecordAsync(AppDbContext db, string component, DateTimeOffset at, string? failure, CancellationToken cancellationToken = default)
    {
        if (failure is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 insert into component_status (component, last_ok_at) values ({component}, {at})
                 on conflict (component) do update set last_ok_at = excluded.last_ok_at
                 """, cancellationToken).ConfigureAwait(false);
            return;
        }
        var text = failure.Length <= MaxFailureLength ? failure : failure[..MaxFailureLength];
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             insert into component_status (component, last_failure_at, last_failure) values ({component}, {at}, {text})
             on conflict (component) do update set last_failure_at = excluded.last_failure_at, last_failure = excluded.last_failure
             """, cancellationToken).ConfigureAwait(false);
    }
}
