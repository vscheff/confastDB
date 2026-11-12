using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Navigation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    [Fact]
    public async Task QuickAccessStartsWithCatalogSuggestionsAndSupportsEachCapacity()
    {
        var access = new QuickAccessService(database, Evaluator, new QuickAccessClock());
        var snapshot = await Evaluator.EvaluateCurrentAsync();
        var initial = await access.LoadAsync(snapshot);

        Assert.Equal(8, initial.Capacity);
        Assert.Equal(NavigationCatalog.ShortcutDestinations.Take(8), initial.Tiles.Select(tile => tile.Destination));
        Assert.All(initial.Tiles, tile => Assert.False(tile.IsPinned));
        foreach (var capacity in new[] { 6, 12, 8 })
        {
            var changed = await access.ChangeAsync(snapshot, new(QuickAccessAction.SetCapacity, Capacity: capacity));
            Assert.Null(changed.Error);
            Assert.Equal(capacity, changed.View.Capacity);
            Assert.Equal(capacity, changed.View.Tiles.Select(tile => tile.Destination.Id).Distinct().Count());
        }
        var invalid = await access.ChangeAsync(snapshot, new(QuickAccessAction.SetCapacity, Capacity: 7));
        Assert.NotNull(invalid.Error);
        Assert.Equal(8, invalid.View.Capacity);
    }

    [Fact]
    public async Task QuickAccessPinsAndDismissalsPersistAndCanBeRestored()
    {
        var time = new QuickAccessClock();
        var access = new QuickAccessService(database, Evaluator, time);
        var snapshot = await Evaluator.EvaluateCurrentAsync();
        async Task<QuickAccessView> Change(QuickAccessAction action, string? id = null)
        {
            var result = await access.ChangeAsync(snapshot, new(action, id));
            Assert.Null(result.Error);
            return result.View;
        }

        var removed = await Change(QuickAccessAction.Remove, "production-tracking");
        Assert.DoesNotContain(removed.Tiles, tile => tile.Destination.Id == "production-tracking");
        Assert.Contains(removed.Removed, item => item.Id == "production-tracking");
        await new NavigationActivityService(database, time).RecordVisitAsync(snapshot, NavigationCatalog.FindDestination("/production-tracking")!);
        time.Now = time.Now.AddDays(1);
        Assert.DoesNotContain((await access.LoadAsync(snapshot)).Tiles, tile => tile.Destination.Id == "production-tracking");

        await Change(QuickAccessAction.Pin, "roles");
        var added = await Change(QuickAccessAction.Pin, "production-tracking");
        Assert.DoesNotContain(added.Removed, item => item.Id == "production-tracking");
        Assert.Equal(new[] { "roles", "production-tracking" }, PinIds(added));
        await Change(QuickAccessAction.Pin, "roles");
        await Change(QuickAccessAction.MoveEarlier, "production-tracking");

        using var secondDevice = new AuthorizationTestSession(database);
        await secondDevice.SignInAsync(rootId);
        var secondAccess = new QuickAccessService(database, secondDevice.Evaluator, time);
        var saved = await secondAccess.LoadAsync(await secondDevice.Evaluator.EvaluateCurrentAsync());
        Assert.Equal(new[] { "production-tracking", "roles" }, PinIds(saved));

        var unpinned = await Change(QuickAccessAction.Unpin, "production-tracking");
        Assert.Equal(new[] { "roles" }, PinIds(unpinned));
        Assert.Contains(unpinned.Tiles, tile => tile.Destination.Id == "production-tracking" && !tile.IsPinned);
        var removedPin = await Change(QuickAccessAction.Remove, "roles");
        Assert.Empty(PinIds(removedPin));
        Assert.Contains(removedPin.Removed, item => item.Id == "roles");
        await Change(QuickAccessAction.Remove, "production-tracking");
        var restored = await Change(QuickAccessAction.Restore, "production-tracking");
        Assert.Equal("production-tracking", restored.Tiles[0].Destination.Id);
        Assert.False(restored.Tiles[0].IsPinned);
        Assert.False((await Change(QuickAccessAction.RestoreAll)).HasRemovedShortcuts);
    }

    [Fact]
    public async Task QuickAccessCapacityCannotDisplacePinsAndResetKeepsActivity()
    {
        var time = new QuickAccessClock();
        var access = new QuickAccessService(database, Evaluator, time);
        var snapshot = await Evaluator.EvaluateCurrentAsync();
        var activity = new NavigationActivityService(database, time);
        await activity.RecordVisitAsync(snapshot, NavigationCatalog.FindDestination("/admin/nominal-tolerance")!);
        var selected = NavigationCatalog.ShortcutDestinations.Take(8).Reverse().Select(item => item.Id).ToArray();
        foreach (var id in selected)
            Assert.Null((await access.ChangeAsync(snapshot, new(QuickAccessAction.Pin, id))).Error);

        var full = await access.ChangeAsync(snapshot, new(QuickAccessAction.Pin, "nominal-tolerance"));
        Assert.Contains("All shortcut slots are pinned", full.Error);
        var tooSmall = await access.ChangeAsync(snapshot, new(QuickAccessAction.SetCapacity, Capacity: 6));
        Assert.Contains("8 saved pins", tooSmall.Error);
        Assert.Equal(selected, PinIds(tooSmall.View));
        await access.ChangeAsync(snapshot, new(QuickAccessAction.SetCapacity, Capacity: 12));
        await access.ChangeAsync(snapshot, new(QuickAccessAction.Remove, "nominal-tolerance"));

        var reset = (await access.ChangeAsync(snapshot, new(QuickAccessAction.Reset))).View;
        Assert.Equal(8, reset.Capacity);
        Assert.False(reset.HasRemovedShortcuts);
        Assert.Empty(PinIds(reset));
        Assert.Equal("nominal-tolerance", reset.Tiles[0].Destination.Id);
        await using var db = database.CreateDbContext();
        Assert.Equal(1, (await db.Set<NavigationUsage>().SingleAsync()).VisitCount);
    }

    [Fact]
    public async Task QuickAccessRanksRecentAndFrequentVisitsAtNextUtcRefresh()
    {
        var time = new QuickAccessClock();
        var access = new QuickAccessService(database, Evaluator, time);
        var activity = new NavigationActivityService(database, time);
        var snapshot = await Evaluator.EvaluateCurrentAsync();
        var gages = NavigationCatalog.FindDestination("/gages")!;
        var customers = NavigationCatalog.FindDestination(NavigationCatalog.GetPath(new Uri("https://confast.example/customers/1234/edit?search=private")))!;
        var parts = NavigationCatalog.FindDestination("/parts")!;

        for (var i = 0; i < 10; i++)
        {
            await activity.RecordVisitAsync(snapshot, gages);
            time.Now = time.Now.AddMinutes(1);
        }
        time.Now = time.Now.AddDays(120);
        var before = await access.LoadAsync(snapshot);
        Assert.Equal("gages", before.Tiles[0].Destination.Id);

        await activity.RecordVisitAsync(snapshot, customers);
        await activity.RecordVisitAsync(snapshot, customers); // A rapid revisit coalesces across circuits.
        await activity.RecordVisitAsync(snapshot, NavigationCatalog.Home);
        time.Now = time.Now.AddMinutes(1);
        await activity.RecordVisitAsync(snapshot, customers);
        await activity.RecordVisitAsync(snapshot, parts);
        Assert.Equal(before.Tiles, (await access.LoadAsync(snapshot)).Tiles.AsEnumerable());

        time.Now = time.Now.AddDays(1);
        var after = await access.LoadAsync(snapshot);
        Assert.Equal(new[] { "customers", "parts", "gages" }, after.Tiles.Take(3).Select(tile => tile.Destination.Id));
        await using var db = database.CreateDbContext();
        var usage = await db.Set<NavigationUsage>().OrderBy(row => row.DestinationId).ToArrayAsync();
        Assert.Equal(new[] { "customers", "gages", "parts" }, usage.Select(row => row.DestinationId));
        Assert.Equal(new long[] { 2, 10, 1 }, usage.Select(row => row.VisitCount));
    }

    [Fact]
    public async Task QuickAccessFiltersRevokedAccessAndIsolatesAccountChanges()
    {
        var role = await Role([Permissions.Parts.Read, Permissions.Customers.Read]);
        var actor = await User(role.Id);
        var account = (await Users.GetUserForEditAsync(actor.Id))!;
        Assert.True(account.Roles.Remove(AppRoles.ReadOnly));
        Assert.True((await Users.UpdateUserAsync(account)).Succeeded);
        var time = new QuickAccessClock();
        using var device = new AuthorizationTestSession(database);
        await device.SignInAsync(actor.Id);
        var access = new QuickAccessService(database, device.Evaluator, time);
        var initial = await device.Evaluator.EvaluateCurrentAsync();
        var newUser = await access.LoadAsync(initial);
        Assert.Equal(new[] { "parts", "customers" }, newUser.Tiles.Select(tile => tile.Destination.Id));
        Assert.Null((await access.ChangeAsync(initial, new(QuickAccessAction.Pin, "parts"))).Error);
        Assert.Null((await access.ChangeAsync(initial, new(QuickAccessAction.Remove, "customers"))).Error);

        var unrelated = await User(role.Id);
        using var otherDevice = new AuthorizationTestSession(database);
        await otherDevice.SignInAsync(unrelated.Id);
        var otherAccess = new QuickAccessService(database, otherDevice.Evaluator, time);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => otherAccess.ChangeAsync(initial, new(QuickAccessAction.Reset)));
        var otherView = await otherAccess.LoadAsync(await otherDevice.Evaluator.EvaluateCurrentAsync());
        Assert.Empty(PinIds(otherView));
        Assert.False(otherView.HasRemovedShortcuts);

        await SetGrants(await Item(role.Id), await Version());
        var denied = await access.ChangeAsync(initial, new(QuickAccessAction.Unpin, "parts"));
        Assert.NotNull(denied.Error);
        Assert.Empty(denied.View.Tiles);
        Assert.Empty(denied.View.Destinations);
        Assert.Empty(denied.View.Removed);
        Assert.Equal(1, denied.View.SavedPinCount);
        await new NavigationActivityService(database, time).RecordVisitAsync(denied.Permissions, NavigationCatalog.FindDestination("/parts")!);
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.Set<NavigationUsage>().ToArrayAsync());
        Assert.Equal(2, await db.Set<NavigationShortcutPreference>().CountAsync(row => row.UserId == actor.Id));

        await SetGrants(await Item(role.Id), await Version(), Permissions.Parts.Read, Permissions.Customers.Read);
        var regained = await access.LoadAsync(await device.Evaluator.EvaluateCurrentAsync());
        Assert.Equal(new[] { "parts" }, PinIds(regained));
        Assert.Equal("customers", Assert.Single(regained.Removed).Id);
    }

    [Fact]
    public async Task QuickAccessConcurrentEditsUseCurrentPinOrderAndDatabaseConstraints()
    {
        var time = new QuickAccessClock();
        using var anotherSession = new AuthorizationTestSession(database);
        await anotherSession.SignInAsync(rootId);
        var snapshot = await Evaluator.EvaluateCurrentAsync();
        var otherSnapshot = await anotherSession.Evaluator.EvaluateCurrentAsync();
        var first = new QuickAccessService(database, Evaluator, time);
        var second = new QuickAccessService(database, anotherSession.Evaluator, time);

        var results = await Task.WhenAll(
            first.ChangeAsync(snapshot, new(QuickAccessAction.Pin, "parts")),
            second.ChangeAsync(otherSnapshot, new(QuickAccessAction.Pin, "parts")),
            second.ChangeAsync(otherSnapshot, new(QuickAccessAction.Pin, "customers")),
            first.ChangeAsync(snapshot, new(QuickAccessAction.SetCapacity, Capacity: 12)));
        Assert.All(results, result => Assert.Null(result.Error));
        var saved = await first.LoadAsync(snapshot);
        Assert.Equal(12, saved.Capacity);
        Assert.Equal(new[] { "customers", "parts" }, PinIds(saved).Order(StringComparer.Ordinal));

        await using var db = database.CreateDbContext();
        var rows = await db.Set<NavigationShortcutPreference>().OrderBy(row => row.PinOrder).ToArrayAsync();
        Assert.Equal(new int?[] { 0, 1 }, rows.Select(row => row.PinOrder));
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO navigation_shortcut_preferences (user_id, destination_id, pin_order) VALUES ({rootId}, 'parts', 2)
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var constraint = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE navigation_preferences SET capacity = 7 WHERE user_id = {rootId}
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, constraint.SqlState);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task QuickAccessIgnoresRetiredDestinationIds()
    {
        var access = new QuickAccessService(database, Evaluator, new QuickAccessClock());
        var snapshot = await Evaluator.EvaluateCurrentAsync();
        var initial = await access.LoadAsync(snapshot);
        await using (var db = database.CreateDbContext())
        {
            var preference = await db.Set<NavigationPreference>().SingleAsync();
            preference.RecommendationOrder = ["retired-destination", .. preference.RecommendationOrder];
            preference.Shortcuts.Add(new() { UserId = rootId, DestinationId = "retired-destination", PinOrder = 0 });
            await db.SaveChangesAsync();
        }

        var current = await access.LoadAsync(snapshot);
        Assert.Equal(0, current.SavedPinCount);
        Assert.Equal(initial.Tiles, current.Tiles.AsEnumerable());
    }

    private static IEnumerable<string> PinIds(QuickAccessView view) => view.Tiles.Where(tile => tile.IsPinned).Select(tile => tile.Destination.Id);

    private sealed class QuickAccessClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
