using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Navigation;

internal sealed class NavigationActivityService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
{
    private static readonly TimeSpan HalfLife = TimeSpan.FromDays(21);
    private static readonly TimeSpan MinimumVisitInterval = TimeSpan.FromSeconds(30);
    private (string UserId, string DestinationId)? previousVisit;

    internal Task ObservePageAsync(EffectivePermissionSnapshot? permissions, NavigationItem? destination)
    {
        if (destination is not { ShowOnHome: true } item)
        {
            previousVisit = null;
            return Task.CompletedTask;
        }

        if (permissions is null) return Task.CompletedTask;
        var visit = (permissions.ActorUserId, item.Id);
        // The router can recreate a page's layout while authorizing a query-only update.
        // Keep the last logical visit at circuit scope so that render still counts once.
        if (previousVisit == visit) return Task.CompletedTask;
        previousVisit = visit;
        return RecordVisitAsync(permissions, item);
    }

    internal async Task RecordVisitAsync(EffectivePermissionSnapshot permissions, NavigationItem destination,
        CancellationToken cancellationToken = default)
    {
        if (!NavigationCatalog.ShortcutDestinations.Contains(destination) || !NavigationCatalog.CanAccess(destination, permissions))
            return;

        var now = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // A single upsert coalesces rapid revisits across circuits and ignores late writes
        // carrying an older timestamp. It never receives a URL or record identifier.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO navigation_usage (user_id, destination_id, visit_count, last_visited_utc, visit_score)
            VALUES ({permissions.ActorUserId}, {destination.Id}, 1, {now}, 1.0)
            ON CONFLICT (user_id, destination_id) DO UPDATE SET
                visit_count = navigation_usage.visit_count + 1,
                last_visited_utc = EXCLUDED.last_visited_utc,
                visit_score = 1.0 + navigation_usage.visit_score * power(0.5::double precision,
                    EXTRACT(EPOCH FROM EXCLUDED.last_visited_utc - navigation_usage.last_visited_utc)::double precision / {HalfLife.TotalSeconds})
            WHERE navigation_usage.last_visited_utc <= {now - MinimumVisitInterval}
            """, cancellationToken);
    }

    internal static string[] Rank(IEnumerable<NavigationUsage> usage, DateTimeOffset now)
    {
        var byId = usage.ToDictionary(row => row.DestinationId, StringComparer.Ordinal);
        return NavigationCatalog.ShortcutDestinations
            .Select(item => (item.Id, Usage: byId.GetValueOrDefault(item.Id)))
            .OrderByDescending(entry => entry.Usage is { } row
                ? row.VisitScore * Math.Pow(0.5, Math.Max(0, (now - row.LastVisitedUtc).TotalSeconds) / HalfLife.TotalSeconds)
                : 0)
            .ThenByDescending(entry => entry.Usage?.LastVisitedUtc)
            .Select(entry => entry.Id)
            .ToArray();
    }
}
