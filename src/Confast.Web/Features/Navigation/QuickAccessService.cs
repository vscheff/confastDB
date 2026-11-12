using System.Collections.Immutable;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Navigation;

internal sealed record QuickAccessTile(NavigationItem Destination, bool IsPinned);

internal sealed record QuickAccessView(int Capacity, int SavedPinCount, ImmutableArray<QuickAccessTile> Tiles,
    ImmutableArray<NavigationItem> Destinations, ImmutableArray<NavigationItem> Removed, bool HasRemovedShortcuts)
{
    public int VisiblePinCount => Tiles.Count(tile => tile.IsPinned);
}

internal enum QuickAccessAction { SetCapacity, Pin, Unpin, Remove, Restore, RestoreAll, Reset, MoveEarlier, MoveLater }

internal sealed record QuickAccessChange(QuickAccessAction Action, string? DestinationId = null, int Capacity = 0);

internal sealed record QuickAccessChangeResult(QuickAccessView View, EffectivePermissionSnapshot Permissions, string? Error);

internal sealed class QuickAccessService(IDbContextFactory<AppDbContext> factory, EffectivePermissionService permissions, TimeProvider clock)
{
    internal const int DefaultCapacity = 8;
    internal static readonly ImmutableArray<int> Capacities = [6, DefaultCapacity, 12];
    private static readonly HashSet<string> KnownIds = NavigationCatalog.ShortcutDestinations
        .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

    internal async Task<QuickAccessView> LoadAsync(EffectivePermissionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var preference = await db.Set<NavigationPreference>().AsNoTracking().Include(x => x.Shortcuts).AsSingleQuery()
            .SingleOrDefaultAsync(x => x.UserId == snapshot.ActorUserId, cancellationToken);
        if (preference?.RecommendationDay is { } day && day >= Today())
            return BuildView(preference, GetDestinations(snapshot));

        return (await SaveOrRefreshAsync(db, snapshot, null, cancellationToken)).View;
    }

    internal async Task<QuickAccessChangeResult> ChangeAsync(EffectivePermissionSnapshot displayedPermissions,
        QuickAccessChange change, CancellationToken cancellationToken = default)
    {
        var current = await permissions.EvaluateCurrentAsync(cancellationToken);
        if (current.ActorUserId != displayedPermissions.ActorUserId)
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidSession);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await SaveOrRefreshAsync(db, current, change, cancellationToken);
    }

    private async Task<QuickAccessChangeResult> SaveOrRefreshAsync(AppDbContext db, EffectivePermissionSnapshot snapshot,
        QuickAccessChange? change, CancellationToken cancellationToken)
    {
        // Every edit and daily refresh takes the same per-account row lock. Read the
        // pins after acquiring it so edits from another device compose with current state.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO navigation_preferences (user_id) VALUES ({snapshot.ActorUserId})
            ON CONFLICT (user_id) DO NOTHING
            """, cancellationToken);
        var preference = await db.Set<NavigationPreference>().FromSqlInterpolated($"""
            SELECT * FROM navigation_preferences WHERE user_id = {snapshot.ActorUserId} FOR UPDATE
            """).SingleAsync(cancellationToken);
        await db.Entry(preference).Collection(x => x.Shortcuts).LoadAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (preference.RecommendationDay is null || preference.RecommendationDay < day)
        {
            var usage = await db.Set<NavigationUsage>().AsNoTracking().Where(x => x.UserId == snapshot.ActorUserId)
                .ToListAsync(cancellationToken);
            // Snapshot the order independently of mutable pins, dismissals and grants.
            // Filter against current preferences and access each time it is displayed.
            preference.RecommendationOrder = NavigationActivityService.Rank(usage, now);
            preference.RecommendationDay = day;
        }

        var destinations = GetDestinations(snapshot);
        var error = change is null ? null : ApplyChange(preference, destinations, change);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(BuildView(preference, destinations), snapshot, error);
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static ImmutableArray<NavigationItem> GetDestinations(EffectivePermissionSnapshot snapshot) =>
        [.. NavigationCatalog.ShortcutDestinations.Where(item => NavigationCatalog.CanAccess(item, snapshot))];

    private static NavigationShortcutPreference[] OrderedPins(NavigationPreference preference) => preference.Shortcuts
        .Where(shortcut => shortcut.PinOrder.HasValue && KnownIds.Contains(shortcut.DestinationId))
        .OrderBy(shortcut => shortcut.PinOrder).ThenBy(shortcut => shortcut.DestinationId, StringComparer.Ordinal).ToArray();

    private static QuickAccessView BuildView(NavigationPreference preference, ImmutableArray<NavigationItem> destinations)
    {
        var allowed = destinations.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var pins = OrderedPins(preference);
        var tiles = pins.Where(pin => allowed.ContainsKey(pin.DestinationId))
            .Select(pin => new QuickAccessTile(allowed[pin.DestinationId], true)).ToList();
        var excluded = preference.Shortcuts.Select(shortcut => shortcut.DestinationId).ToHashSet(StringComparer.Ordinal);
        // Include newly deployed destinations even if today's snapshot predates the deployment.
        tiles.AddRange(preference.RecommendationOrder.Concat(destinations.Select(item => item.Id))
            .Distinct(StringComparer.Ordinal).Where(id => !excluded.Contains(id) && allowed.ContainsKey(id))
            .Take(preference.Capacity - tiles.Count).Select(id => new QuickAccessTile(allowed[id], false)));

        var removed = preference.Shortcuts.Where(shortcut => shortcut.PinOrder is null && KnownIds.Contains(shortcut.DestinationId))
            .Select(shortcut => shortcut.DestinationId).ToHashSet(StringComparer.Ordinal);
        return new(preference.Capacity, pins.Length, [.. tiles], destinations,
            [.. destinations.Where(item => removed.Contains(item.Id))], removed.Count > 0);
    }

    private static string? ApplyChange(NavigationPreference preference, ImmutableArray<NavigationItem> destinations,
        QuickAccessChange change)
    {
        var pins = OrderedPins(preference);
        switch (change.Action)
        {
            case QuickAccessAction.SetCapacity:
                if (!Capacities.Contains(change.Capacity)) return "Choose 6, 8, or 12 shortcut slots.";
                if (change.Capacity < pins.Length) return $"You have {pins.Length} saved pins. Unpin shortcuts before reducing capacity to {change.Capacity}.";
                preference.Capacity = change.Capacity;
                return null;
            case QuickAccessAction.RestoreAll:
                preference.Shortcuts.RemoveAll(entry => entry.PinOrder is null);
                return null;
            case QuickAccessAction.Reset:
                preference.Shortcuts.Clear();
                preference.Capacity = DefaultCapacity;
                return null;
        }

        if (change.DestinationId is not { } id || !destinations.Any(item => item.Id == id))
            return "This destination is no longer available to your account.";

        var shortcut = preference.Shortcuts.SingleOrDefault(entry => entry.DestinationId == id);
        switch (change.Action)
        {
            case QuickAccessAction.Pin:
                if (shortcut?.PinOrder is not null) return null;
                if (pins.Length >= preference.Capacity) return "All shortcut slots are pinned. Unpin a shortcut or increase capacity.";
                for (var i = 0; i < pins.Length; i++) pins[i].PinOrder = i;
                (shortcut ?? AddShortcut(preference, id)).PinOrder = pins.Length;
                break;
            case QuickAccessAction.Unpin:
                if (shortcut?.PinOrder is not null) preference.Shortcuts.Remove(shortcut);
                break;
            case QuickAccessAction.Remove:
                (shortcut ?? AddShortcut(preference, id)).PinOrder = null;
                break;
            case QuickAccessAction.Restore:
                if (shortcut is { PinOrder: null }) preference.Shortcuts.Remove(shortcut);
                break;
            case QuickAccessAction.MoveEarlier:
            case QuickAccessAction.MoveLater:
                var visiblePins = pins.Where(pin => destinations.Any(item => item.Id == pin.DestinationId)).ToArray();
                var index = Array.IndexOf(visiblePins, shortcut);
                var target = index + (change.Action == QuickAccessAction.MoveEarlier ? -1 : 1);
                if (index >= 0 && target >= 0 && target < visiblePins.Length)
                    (visiblePins[index].PinOrder, visiblePins[target].PinOrder) = (visiblePins[target].PinOrder, visiblePins[index].PinOrder);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        return null;
    }

    private static NavigationShortcutPreference AddShortcut(NavigationPreference preference, string id)
    {
        var shortcut = new NavigationShortcutPreference { UserId = preference.UserId, DestinationId = id };
        preference.Shortcuts.Add(shortcut);
        return shortcut;
    }
}
