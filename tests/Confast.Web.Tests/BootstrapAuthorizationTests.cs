using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Confast.Web.Tests;

internal sealed class TestHostEnvironment(string name = "Development") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "Confast.Web";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

[Collection(PostgresCollection.Name)]
public sealed class BootstrapAuthorizationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    private ServiceProvider Services(string environment = "Development", string username = "installation.test")
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environment));
        services.AddOptions<BootstrapAdminOptions>().Configure(x => { x.Username = username; x.Email = "install@fixture.invalid"; x.Password = "Installation-Fixture-9!"; });
        services.AddOptions<BrowserTestUserOptions>().Configure(x => { x.Username = username; x.Password = "Browser-Fixture-9!"; });
        services.AddDbContextFactory<AppDbContext>(x => x.UseNpgsql(database.ConnectionString));
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<AppDbContext>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task InstallationIsAtomic_CreateOnly_AndNeverSelectsRoot()
    {
        await using var services = Services();
        await Task.WhenAll(IdentityBootstrapper.CreateInitialAdministratorAsync(services), IdentityBootstrapper.CreateInitialAdministratorAsync(services));
        await using var db = database.CreateDbContext();
        var user = await db.Users.SingleAsync(); var originalHash = user.PasswordHash;
        Assert.Equal(AuthorizationReadiness.PendingRoot, (await db.AuthorizationState.SingleAsync()).Readiness);
        Assert.Null((await db.AuthorizationState.SingleAsync()).RootUserId);
        Assert.True(await db.UserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == AppRoles.AdministratorId));
        await db.UserRoles.Where(x => x.UserId == user.Id).ExecuteDeleteAsync();
        await db.RolePermissions.Where(x => x.RoleId == AppRoles.QualityId && x.PermissionKey == Permissions.Chat.Access).ExecuteDeleteAsync();
        await db.RoleInheritance.Where(x => x.ChildRoleId == AppRoles.AdministratorId && x.ParentRoleId == AppRoles.QualityId).ExecuteDeleteAsync();
        user.IsActive = false; await db.SaveChangesAsync();
        var epoch = (await db.AuthorizationState.AsNoTracking().SingleAsync()).GlobalEpoch;
        await IdentityBootstrapper.CreateInitialAdministratorAsync(services);
        await IdentityBootstrapper.EnsureBrowserTestUserAsync(services);
        await db.Entry(user).ReloadAsync();
        Assert.False(user.IsActive); Assert.Equal(originalHash, user.PasswordHash);
        Assert.Empty(await db.UserRoles.Where(x => x.UserId == user.Id).ToListAsync());
        Assert.False(await db.RolePermissions.AnyAsync(x => x.RoleId == AppRoles.QualityId && x.PermissionKey == Permissions.Chat.Access));
        Assert.False(await db.RoleInheritance.AnyAsync(x => x.ChildRoleId == AppRoles.AdministratorId && x.ParentRoleId == AppRoles.QualityId));
        Assert.Equal(epoch, (await db.AuthorizationState.AsNoTracking().SingleAsync()).GlobalEpoch);
        Assert.Single(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task ReadyStartup_PreservesRootIdentityCredentialAndGraph_AndBrowserConfigurationCannotElevateIt()
    {
        using var session = new AuthorizationTestSession(database);
        var id = await session.ProvisionRootAsync();
        await using var db = database.CreateDbContext();
        var root = await db.Users.SingleAsync(x => x.Id == id);
        var originalHash = root.PasswordHash;
        await db.UserRoles.Where(x => x.UserId == id && x.RoleId == AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        var before = await db.AuthorizationState.AsNoTracking().SingleAsync();
        await using var services = Services(username: root.UserName!);
        await IdentityBootstrapper.CreateInitialAdministratorAsync(services);
        await IdentityBootstrapper.EnsureBrowserTestUserAsync(services);
        var after = await db.AuthorizationState.AsNoTracking().SingleAsync();
        Assert.Equal(id, after.RootUserId); Assert.Equal(before.InstallationGeneration, after.InstallationGeneration);
        Assert.Equal(before.GlobalEpoch, after.GlobalEpoch); await db.Entry(root).ReloadAsync();
        Assert.Equal(originalHash, root.PasswordHash);
        Assert.Equal(AppRoles.RootId, Assert.Single(await db.UserRoles.Where(x => x.UserId == id).ToListAsync()).RoleId);
    }

    [Fact]
    public async Task DevelopmentDateMutationRequiresCurrentProtectedAuthority_AndCannotRunInProduction()
    {
        using var session = new AuthorizationTestSession(database);
        await session.SignInAsync(await session.ProvisionRootAsync());
        var clock = new Confast.Web.Time.DevelopmentDateOverrideTimeProvider();
        var evaluator = session.Evaluator;
        var guard = new ApplicationAuthorization(evaluator, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        var utility = new DevelopmentUtilities(new TestHostEnvironment(), guard, database, clock);
        await utility.SetDateAsync(new DateOnly(2026, 1, 2));
        Assert.Equal(1, evaluator.SnapshotCount); Assert.Equal(new DateOnly(2026, 1, 2), clock.CurrentDate);
        var production = new DevelopmentUtilities(new TestHostEnvironment("Production"), guard, database, clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.SetDateAsync(null));
        await using var db = database.CreateDbContext();
        var ordinary = new ApplicationUser { UserName = "date.fixture", DisplayName = "Date", SecurityStamp = Guid.NewGuid().ToString() };
        db.Users.Add(ordinary); await db.SaveChangesAsync(); await session.SignInAsync(ordinary.Id);
        ((System.Security.Claims.ClaimsIdentity)session.Principal.Identity!).AddClaim(new(System.Security.Claims.ClaimTypes.Role, AppRoles.Administrator));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => utility.SetDateAsync(null));
        Assert.Equal(new DateOnly(2026, 1, 2), clock.CurrentDate);
    }

    [Fact]
    public async Task BrowserProvisioningIsDevelopmentOnly_CreateOnly_AndDoesNotRepairMissingRoles()
    {
        await using (var production = Services("Production")) await IdentityBootstrapper.EnsureBrowserTestUserAsync(production);
        await using var db = database.CreateDbContext(); Assert.Empty(await db.Users.ToListAsync());
        await using var development = Services();
        await IdentityBootstrapper.EnsureBrowserTestUserAsync(development);
        var user = await db.Users.SingleAsync(); var hash = user.PasswordHash;
        Assert.False(await db.UserRoles.AnyAsync(x => x.RoleId == AppRoles.RootId));
        user.IsActive = false; await db.SaveChangesAsync();
        await db.UserRoles.Where(x => x.UserId == user.Id).ExecuteDeleteAsync();
        await IdentityBootstrapper.EnsureBrowserTestUserAsync(development);
        await db.Entry(user).ReloadAsync();
        Assert.False(user.IsActive); Assert.Equal(hash, user.PasswordHash);
        Assert.Empty(await db.UserRoles.Where(x => x.UserId == user.Id).ToListAsync());
    }
}
