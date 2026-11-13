using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Navigation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Confast.Web.Tests;

public sealed class AppearancePreferenceTests
{
    [Theory]
    [InlineData("#ABCDEF", "#ABCDEF")]
    [InlineData("  #Abc  ", "#AABBCC")]
    [InlineData("14f0dd", "#14F0DD")]
    [InlineData("#001", "#000011")]
    [InlineData("#abcd", null)]
    [InlineData("#12345678", null)]
    [InlineData("##123", null)]
    [InlineData("#１２３", null)]
    [InlineData("red", null)]
    [InlineData("#ff0;}", null)]
    [InlineData(null, null)]
    public void NormalizeAccentAcceptsOnlyHexDigits(string? input, string? expected) =>
        Assert.Equal(expected, AppearancePreference.NormalizeAccent(input));

    [Theory]
    [InlineData("light", "light")]
    [InlineData("DARK", "dark")]
    [InlineData("system", "system")]
    [InlineData("retired", "system")]
    [InlineData("1", "system")]
    [InlineData(null, "system")]
    public void StoredModeResolvesIndependentlyOfAccent(string? stored, string expected)
    {
        var invalidAccent = AppearancePreference.FromStored(stored, "#badhex");
        Assert.Equal(expected, invalidAccent.ModeName);
        Assert.Equal(AppearancePreference.DefaultAccent, invalidAccent.AccentColor);
        Assert.Equal("#FFEE00", AppearancePreference.FromStored(stored, "#fe0").AccentColor);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class AppearancePersistenceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExistingAccountsReadDefaultsAndIndependentWritesPreserveBothFields()
    {
        using var first = await CreateAccountAsync();
        using var other = await CreateAccountAsync();
        using var second = new AuthorizationTestSession(database) { Principal = first.Principal };
        var preferences = Service(first);
        Assert.Equal(AppearancePreference.Default, await preferences.LoadAsync());
        await using (var db = database.CreateDbContext())
            Assert.False(await db.Set<NavigationPreference>().AnyAsync());

        await Task.WhenAll(preferences.SaveAsync(AppearanceMode.Dark, null),
            Service(second).SaveAsync(null, "#fc0"));
        var saved = new AppearancePreference(AppearanceMode.Dark, "#FFCC00");
        Assert.Equal(saved, await Service(second).LoadAsync(first.Principal));
        Assert.Equal(AppearancePreference.Default, await Service(other).LoadAsync());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => preferences.SaveAsync((AppearanceMode)12, null));
        await Assert.ThrowsAsync<ArgumentException>(() => preferences.SaveAsync(null, "#this"));
        Assert.Equal(saved, await preferences.LoadAsync());
        Assert.Equal(new(AppearanceMode.Light, "#FFCC00"), await preferences.SaveAsync(AppearanceMode.Light, null));
        Assert.Equal(AppearancePreference.Default,
            await preferences.SaveAsync(AppearancePreference.Default.Mode, AppearancePreference.DefaultAccent));
        Assert.Equal(AppearancePreference.Default, await Service(second).LoadAsync());
    }

    [Fact]
    public async Task InvalidStoredPreferencesFallBackAndRestoreKeepsQuickAccess()
    {
        using var owner = new AuthorizationTestSession(database);
        await owner.SignInAsync(await owner.ProvisionRootAsync());
        var quickAccess = new QuickAccessService(database, owner.Evaluator, TimeProvider.System);
        var permissions = await owner.Evaluator.EvaluateCurrentAsync();
        Assert.Null((await quickAccess.ChangeAsync(permissions, new(QuickAccessAction.SetCapacity, Capacity: 12))).Error);
        Assert.Null((await quickAccess.ChangeAsync(permissions, new(QuickAccessAction.Pin, "parts"))).Error);

        await using (var db = database.CreateDbContext())
            await db.Set<NavigationPreference>().Where(x => x.UserId == owner.ActorId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.AppearanceMode, "legacy").SetProperty(x => x.AccentColor, "#wrong"));
        var preferences = Service(owner);
        Assert.Equal(AppearancePreference.Default, await preferences.LoadAsync());
        var chosen = AppearancePreference.Presets.Single(preset => preset.Name == "Purple");
        Assert.Equal(new(AppearanceMode.Light, chosen.Color), await preferences.SaveAsync(AppearanceMode.Light, chosen.Color));
        await preferences.SaveAsync(AppearanceMode.System, AppearancePreference.DefaultAccent);

        var restored = await quickAccess.LoadAsync(permissions);
        Assert.Equal(12, restored.Capacity);
        Assert.Contains(restored.Tiles, tile => tile is { IsPinned: true, Destination.Id: "parts" });
        Assert.Null((await quickAccess.ChangeAsync(permissions, new(QuickAccessAction.Reset))).Error);
        await preferences.SaveAsync(AppearanceMode.Dark, null);
        Assert.Null((await quickAccess.ChangeAsync(permissions, new(QuickAccessAction.Pin, "customers"))).Error);
        Assert.Equal(AppearanceMode.Dark, (await preferences.LoadAsync()).Mode);
    }

    [Fact]
    public async Task AnonymousInactiveAndExpiredSessionsCannotWrite()
    {
        using var anonymous = new AuthorizationTestSession(database);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(anonymous).LoadAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Service(anonymous).SaveAsync(AppearanceMode.Dark, null));

        using var account = await CreateAccountAsync();
        var preferences = Service(account);
        await using var db = database.CreateDbContext();
        var user = await db.Users.SingleAsync(x => x.Id == account.ActorId);
        user.SecurityStamp = Guid.NewGuid().ToString();
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => preferences.SaveAsync(AppearanceMode.Dark, null));
        await account.SignInAsync(user.Id);
        user.IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => preferences.LoadAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => preferences.SaveAsync(AppearanceMode.Dark, null));
    }

    private AppearancePreferenceService Service(AuthorizationTestSession session) =>
        new(database, session, Options.Create(new IdentityOptions()));

    private async Task<AuthorizationTestSession> CreateAccountAsync()
    {
        await using var db = database.CreateDbContext();
        var user = new ApplicationUser { IsActive = true, SecurityStamp = Guid.NewGuid().ToString(), DisplayName = "Appearance test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new(database) { Principal = AuthorizationTestSession.UserPrincipal(user) };
    }
}
