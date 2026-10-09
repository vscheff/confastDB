using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DelegatedAdministrationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly AuthorizationTestSession session = new(database);
    private readonly TestClock clock = new();
    private ServiceProvider services = null!;
    private string rootId = "";
    private static readonly string[] Management = [Permissions.Roles.Read, Permissions.Roles.Create, Permissions.Roles.Update,
        Permissions.Roles.Delete, Permissions.Roles.ManagePermissions, Permissions.Roles.ManageInheritance,
        Permissions.Users.Read, Permissions.Users.Create, Permissions.Users.Update, Permissions.Users.Delete,
        Permissions.Users.ManageRoles, Permissions.Users.ResetPasswords];
    private UserAdministrationService Users => services.GetRequiredService<UserAdministrationService>();
    private RoleAdministrationService Roles => services.GetRequiredService<RoleAdministrationService>();
    private EffectivePermissionService Evaluator => services.GetRequiredService<EffectivePermissionService>();

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        rootId = await session.ProvisionRootAsync(); await session.SignInAsync(rootId);
        services = BuildServices();
    }
    public async Task DisposeAsync() { session.Dispose(); await services.DisposeAsync(); }
    private ServiceProvider BuildServices()
    {
        var collection = new ServiceCollection(); collection.AddLogging(); collection.AddHttpContextAccessor(); collection.AddAuthentication();
        collection.AddSingleton<TimeProvider>(clock); collection.AddSingleton<IDbContextFactory<AppDbContext>>(database);
        collection.AddScoped(_ => database.CreateDbContext()); collection.AddSingleton<AuthenticationStateProvider>(session);
        collection.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        collection.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
        collection.AddConfastAuthorization(); collection.AddScoped<UserAdministrationService>();
        return collection.BuildServiceProvider();
    }
    private async Task<AuthorizationVersion> Version() => (await Evaluator.EvaluateCurrentAsync()).Version;
    private async Task<ApplicationUser> User(params string[] assigned)
    {
        var name = Guid.NewGuid().ToString();
        var user = new ApplicationUser { UserName = name, NormalizedUserName = name.ToUpperInvariant(), Email = name + "@example.test",
            NormalizedEmail = (name + "@example.test").ToUpperInvariant(), DisplayName = name, SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() };
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, "Original-Test-Password-9!");
        await using var db = database.CreateDbContext(); db.Users.Add(user); await db.SaveChangesAsync();
        db.UserRoles.AddRange(assigned.Select(id => new IdentityUserRole<string> { UserId = user.Id, RoleId = id })); await db.SaveChangesAsync();
        return user;
    }
    private async Task<ApplicationRole> Role(string[]? keys = null, bool enabled = true, params string[] parents)
    {
        var name = "Test Role " + Guid.NewGuid(); var role = new ApplicationRole(name) { NormalizedName = name.ToUpperInvariant(), IsEnabled = enabled };
        await using var db = database.CreateDbContext(); db.Roles.Add(role); await db.SaveChangesAsync();
        db.RolePermissions.AddRange((keys ?? []).Select(k => new RolePermission { RoleId = role.Id, PermissionKey = k }));
        db.RoleInheritance.AddRange(parents.Select(id => new RoleInheritance { ChildRoleId = role.Id, ParentRoleId = id })); await db.SaveChangesAsync();
        return role;
    }
    private async Task<ApplicationUser> Manager(params string[] lower)
    { var anchor = await Role(Management, true, lower); var user = await User(anchor.Id); await session.SignInAsync(user.Id); return user; }
    private async Task<RoleAdministrationItem> Item(string id) => (await Roles.GetRolesAsync()).Roles.Single(x => x.Id == id);
    private Task SetGrants(RoleAdministrationItem item, AuthorizationVersion v, params string[] keys) => Roles.SetGrantsAsync(item.Id, keys, item.ConcurrencyStamp, v);

    [Fact]
    public async Task AdministratorPromotesPeerAndLower_WithoutHardcodedAssignmentException()
    {
        var actor = await User(AppRoles.AdministratorId); var target = await User(); await session.SignInAsync(actor.Id);
        var edit = (await Users.GetUserForEditAsync(target.Id))!; edit.Roles.Add(AppRoles.Administrator);
        Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
        edit = (await Users.GetUserForEditAsync(target.Id))!; edit.Roles.Add(AppRoles.Quality);
        Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
        await using var db = database.CreateDbContext();
        Assert.Contains(await db.UserRoles.Where(x => x.UserId == target.Id).ToListAsync(), x => x.RoleId == AppRoles.AdministratorId);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("root")]
    [InlineData("baseline")]
    [InlineData("unrelated")]
    [InlineData("higher")]
    [InlineData("disabled")]
    public async Task AssignmentRejectsProtectedSelfUnrelatedHigherAndDisabledAdditions(string scenario)
    {
        var lower = await Role(); var actor = await Manager(lower.Id); var target = await User();
        var edit = (await Users.GetUserForEditAsync(scenario == "self" ? actor.Id : target.Id))!;
        if (scenario == "self") edit.Roles.Add(lower.Name!);
        if (scenario == "root") edit.Roles.Add(AppRoles.Root);
        if (scenario == "baseline") edit.Roles.Remove(AppRoles.ReadOnly);
        if (scenario == "unrelated") edit.Roles.Add((await Role(Management)).Name!);
        if (scenario == "higher") edit.Roles.Add(AppRoles.Administrator);
        if (scenario == "disabled")
        { await using var db = database.CreateDbContext(); await db.Roles.Where(x => x.Id == lower.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false)); edit.Roles.Add(lower.Name!); }
        // Refresh only the expected global version; the role changes still must satisfy current authority.
        edit.Version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.UpdateUserAsync(edit));
        await using var verify = database.CreateDbContext();
        Assert.Equal(1, await verify.UserRoles.CountAsync(x => x.UserId == target.Id));
    }

    [Fact]
    public async Task UnauthorizedExistingAssignmentIsPreserved_AndReplacementRemovalIsRejected()
    {
        var lower = await Role(); var unrelated = await Role(Management); var target = await User(lower.Id, unrelated.Id);
        await Manager(lower.Id);
        var edit = (await Users.GetUserForEditAsync(target.Id))!; edit.Roles.Remove(lower.Name!);
        Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
        edit = (await Users.GetUserForEditAsync(target.Id))!;
        Assert.Contains(unrelated.Name!, edit.Roles); edit.Roles.Remove(unrelated.Name!);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.UpdateUserAsync(edit));
        Assert.Contains(unrelated.Name!, (await Users.GetUserForEditAsync(target.Id))!.Roles);
    }

    [Fact]
    public async Task MultipleAssignmentsDoNotInventPaths_EnvelopeAlsoMustPass()
    {
        var dormant = await Role([Permissions.Parts.Create], false);
        var lower = await Role([], true, dormant.Id);
        var first = await Role(Management, true, lower.Id); var second = await Role([Permissions.Inspections.Create]);
        var unrelated = await Role([Permissions.Inspections.Create]); var actor = await User(first.Id, second.Id); var target = await User();
        await session.SignInAsync(actor.Id);
        foreach (var name in new[] { lower.Name!, unrelated.Name! })
        { var edit = (await Users.GetUserForEditAsync(target.Id))!; edit.Roles.Add(name); await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.UpdateUserAsync(edit)); }
    }

    [Fact]
    public async Task QualityLeadAssignsActualLowerInspectorAndDirectPeer_WhilePermissionEditingPeerIsDenied()
    {
        var inspector = await Role([Permissions.Inspections.Create]); var lead = await Role(Management, true, inspector.Id);
        var actor = await User(lead.Id); var target = await User(); await session.SignInAsync(actor.Id);
        var edit = (await Users.GetUserForEditAsync(target.Id))!; edit.Roles.Add(inspector.Name!);
        Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
        edit = (await Users.GetUserForEditAsync(target.Id))!; edit.Roles.Add(lead.Name!);
        Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
        var item = await Item(lead.Id); var version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => SetGrants(item, version, Management));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("active")]
    [InlineData("username")]
    [InlineData("reset")]
    [InlineData("delete")]
    public async Task OrdinarySelfAdministrationCannotChangeSecuritySensitiveFields(string change)
    {
        var actor = await Manager(); var edit = (await Users.GetUserForEditAsync(actor.Id))!;
        if (change == "email") edit.Email = "self.administrative@example.test";
        if (change == "active") edit.IsActive = false;
        if (change == "username") edit.Username = "self.administrative";
        Func<Task> action = change switch { "reset" => async () => { await Users.GeneratePasswordResetTokenAsync(actor.Id, edit.Version, edit.ConcurrencyStamp); },
            "delete" => async () => { await Users.DeleteUserAsync(actor.Id, edit.Version, edit.ConcurrencyStamp); }, _ => async () => { await Users.UpdateUserAsync(edit); } };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalPermissionEnvelopeDoesNotMakeUnrelatedAccountManageable(bool dormant)
    {
        var unrelated = await Role(Management, !dormant); var target = await User(unrelated.Id); await Manager();
        var edit = (await Users.GetUserForEditAsync(target.Id))!;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.GeneratePasswordResetTokenAsync(target.Id, edit.Version, edit.ConcurrencyStamp));
        edit.Email = "takeover@example.test";
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.UpdateUserAsync(edit));
    }

    [Fact]
    public async Task CachedActorIsRevalidatedUnderLockEvenWithCurrentExpectedEpoch()
    {
        var lower = await Role(); var actor = await Manager(lower.Id); var item = await Item(lower.Id);
        await Evaluator.EvaluateCurrentAsync();
        AuthorizationVersion currentVersion;
        await using (var db = database.CreateDbContext())
        {
            await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "revoked-session"));
            var state = await db.AuthorizationState.AsNoTracking().SingleAsync();
            currentVersion = new(state.InstallationGeneration, state.CatalogVersion, state.GlobalEpoch);
        }
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.UpdateAsync(new(item.Id, "Not Authorized", null, true, item.ConcurrencyStamp, currentVersion)));
        Assert.Equal(AuthorizationDenial.InvalidSession, denied.Denial);
        await using var verify = database.CreateDbContext(); Assert.Equal(item.Name, await verify.Roles.Where(x => x.Id == item.Id).Select(x => x.Name).SingleAsync());
    }

    [Fact]
    public async Task CreateAttachesFreshEmptySubordinate_WithoutAssignmentsOrImportedParents()
    {
        var actor = await Manager();
        var view = await Roles.GetRolesAsync(); var anchor = Assert.Single(view.Roles, x => x.CanAnchor);
        var before = Evaluator.ComputationCount;
        var id = await Roles.CreateAsync(new("Quality Inspector", "Empty subordinate", anchor.Id, view.Version));
        Assert.Equal(before + 1, Evaluator.ComputationCount);
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.RolePermissions.Where(x => x.RoleId == id).ToListAsync());
        Assert.Empty(await db.UserRoles.Where(x => x.RoleId == id).ToListAsync());
        Assert.Empty(await db.RoleInheritance.Where(x => x.ChildRoleId == id).ToListAsync());
        Assert.True(await db.RoleInheritance.AnyAsync(x => x.ChildRoleId == anchor.Id && x.ParentRoleId == id));
        Assert.True((await Item(id)).CanEdit);
        var freshVersion = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.CreateAsync(new("Independent", null, null, freshVersion)));
    }

    [Fact]
    public async Task RootCreatesIndependentRoleAndOrdinaryRoleNamesRemainUnique()
    {
        var id = await Roles.CreateAsync(new("Independent Role", null, null, await Version()));
        var view = await Roles.GetRolesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Roles.CreateAsync(new("independent role", null, null, view.Version)));
        await using var db = database.CreateDbContext();
        Assert.False(await db.RoleInheritance.AnyAsync(x => x.ChildRoleId == id || x.ParentRoleId == id));
        Assert.False(await db.UserRoles.AnyAsync(x => x.RoleId == id));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("disabled direct")]
    [InlineData("baseline")]
    [InlineData("root")]
    [InlineData("unrelated")]
    public async Task PeerAndProtectedRoleEditingIsDenied(string target)
    {
        var held = await Role([], false); var actor = await Manager(held.Id);
        await using (var db = database.CreateDbContext()) { db.UserRoles.Add(new() { UserId = actor.Id, RoleId = held.Id }); await db.SaveChangesAsync(); }
        var view = await Roles.GetRolesAsync();
        var id = target switch { "direct" => view.Roles.Single(x => x.CanAnchor).Id, "disabled direct" => held.Id,
            "baseline" => AppRoles.ReadOnlyId, "root" => AppRoles.RootId, _ => AppRoles.ProductionId };
        var item = view.Roles.Single(x => x.Id == id);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => SetGrants(item, view.Version, Permissions.Users.Read));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.UpdateAsync(new(id, "Renamed", null, item.IsEnabled, item.ConcurrencyStamp, view.Version)));
    }

    [Fact]
    public async Task SafeInheritedSourceEditingSucceeds_AndSelfRevocationAffectsNextEvent()
    {
        var lower = await Role(Management); var anchor = await Role([], true, lower.Id); var actor = await User(anchor.Id);
        await session.SignInAsync(actor.Id);
        var item = await Item(lower.Id); var version = await Version();
        await SetGrants(item, version, Management.Except([Permissions.Roles.ManagePermissions]).ToArray());
        item = await Item(lower.Id); version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => SetGrants(item, version, Management));
        Assert.False((await Evaluator.EvaluateCurrentAsync()).Has(Permissions.Roles.ManagePermissions));
    }

    [Fact]
    public async Task UnavailableGrantRemovalRequiresEntireFinalDormantEnvelopeToBeWithinAuthority()
    {
        var lower = await Role([Permissions.Parts.Create, Permissions.Parts.Update], false); await Manager(lower.Id);
        var item = await Item(lower.Id); var version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => SetGrants(item, version, Permissions.Parts.Update));
        await SetGrants(item, version);
        item = await Item(lower.Id); version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => SetGrants(item, version, Permissions.Parts.Create));
    }

    [Fact]
    public async Task InheritedUnavailableGrantCannotBeHiddenByRemovingDirectGrants()
    {
        var dormant = await Role([Permissions.Parts.Create], false); var lower = await Role([Permissions.Parts.Update], false, dormant.Id);
        await Manager(lower.Id);
        var item = await Item(lower.Id); var version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => SetGrants(item, version));
    }

    [Fact]
    public async Task EnableDormantRoleCannotIncreaseActorPermissions_AndRequiresBothKeys()
    {
        var lower = await Role([Permissions.Parts.Create], false); await Manager(lower.Id);
        var item = await Item(lower.Id); var version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.UpdateAsync(new(lower.Id, item.Name, null, true, item.ConcurrencyStamp, version)));
        var enabledLower = await Role(); var anchor = await Role([Permissions.Roles.Read, Permissions.Roles.Update], true, enabledLower.Id);
        await session.SignInAsync((await User(anchor.Id)).Id); item = await Item(enabledLower.Id); version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.UpdateAsync(new(item.Id, item.Name, null, false, item.ConcurrencyStamp, version)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerAnchorReachCannotExpandEvenWhenUnionOfHeldReachIsUnchanged(bool disabledAnchor)
    {
        var existing = await Role(); var branch = await Role();
        var first = await Role(Management, !disabledAnchor, branch.Id); var second = await Role(Management, true, existing.Id, first.Id);
        var actor = await User(first.Id, second.Id); await session.SignInAsync(actor.Id);
        var item = await Item(branch.Id); var version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.SetParentsAsync(branch.Id, [existing.Id], item.ConcurrencyStamp, version));
        await using var db = database.CreateDbContext(); Assert.False(await db.RoleInheritance.AnyAsync(x => x.ChildRoleId == branch.Id && x.ParentRoleId == existing.Id));
    }

    [Fact]
    public async Task CreatingIntermediateRoleDoesNotPermitAdoptingUnrelatedExistingAuthority()
    {
        var unrelated = await Role(Management); await Manager();
        var view = await Roles.GetRolesAsync(); var anchor = view.Roles.Single(x => x.CanAnchor);
        var fresh = await Roles.CreateAsync(new("Intermediate", null, anchor.Id, view.Version));
        var item = await Item(fresh); var version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.SetParentsAsync(fresh, [unrelated.Id], item.ConcurrencyStamp, version));
        await SetGrants(item, version, Permissions.Users.Read);
        item = await Item(fresh); version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.SetParentsAsync(fresh, [unrelated.Id], item.ConcurrencyStamp, version));
    }

    [Fact]
    public async Task RootSupportsMultipleParents_ButCyclesSelfDuplicateAndProtectedEdgesFail()
    {
        var a = await Role(); var b = await Role(); var c = await Role();
        var item = await Item(a.Id); await Roles.SetParentsAsync(a.Id, [b.Id, c.Id], item.ConcurrencyStamp, await Version());
        item = await Item(b.Id); var version = await Version();
        await Assert.ThrowsAsync<ArgumentException>(() => Roles.SetParentsAsync(b.Id, [a.Id], item.ConcurrencyStamp, version));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.SetParentsAsync(b.Id, [b.Id], item.ConcurrencyStamp, version));
        await Assert.ThrowsAsync<ArgumentException>(() => Roles.SetParentsAsync(b.Id, [c.Id, c.Id], item.ConcurrencyStamp, version));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.SetParentsAsync(b.Id, [AppRoles.ReadOnlyId], item.ConcurrencyStamp, version));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => SetGrants(item, version, Permissions.Authorization.ManageSecurity));
    }

    [Fact]
    public async Task DeletionRejectsAnyAssignment_ThenRemovesOnlyIncidentEdgesAndGrants()
    {
        var parent = await Role([Permissions.Users.Read]); var target = await Role([], true, parent.Id);
        var descendant = await Role([], true, target.Id); var assigned = await User(target.Id); await Manager(descendant.Id);
        var item = await Item(target.Id); var version = await Version();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Roles.DeleteAsync(target.Id, item.ConcurrencyStamp, version));
        var edit = (await Users.GetUserForEditAsync(assigned.Id))!; edit.Roles.Remove(target.Name!);
        Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
        item = await Item(target.Id); await Roles.DeleteAsync(target.Id, item.ConcurrencyStamp, await Version());
        await using var db = database.CreateDbContext();
        Assert.False(await db.Roles.AnyAsync(x => x.Id == target.Id)); Assert.True(await db.Roles.AnyAsync(x => x.Id == parent.Id));
        Assert.False(await db.RoleInheritance.AnyAsync(x => x.ChildRoleId == descendant.Id));
        Assert.True(await db.AuthorizationChangeHistory.AnyAsync(x => x.Purpose == "Roles.Delete"));
    }

    [Fact]
    public async Task ConcurrentEditorsWithSameVersion_OnlyOneCommits_WithOneAuditAndFinalEpoch()
    {
        var lower = await Role(); await Manager(lower.Id); var item = await Item(lower.Id); var version = await Version();
        async Task<Exception?> Edit(string name)
        {
            await using var scope = services.CreateAsyncScope();
            return await Record.ExceptionAsync(() => scope.ServiceProvider.GetRequiredService<RoleAdministrationService>()
                .UpdateAsync(new(lower.Id, name, null, true, item.ConcurrencyStamp, version)));
        }
        var outcomes = await Task.WhenAll(Edit("Winner One"), Edit("Winner Two"));
        Assert.Single(outcomes, x => x is null); Assert.Single(outcomes, x => x is DbUpdateConcurrencyException);
        await using var db = database.CreateDbContext();
        var history = Assert.Single(await db.AuthorizationChangeHistory.Where(x => x.Purpose == "Roles.Update").ToListAsync());
        Assert.Equal(version.Epoch, history.PreviousEpoch); Assert.Equal(await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync(), history.ResultingEpoch);
    }

    [Fact]
    public async Task StaleEntityAndVersionReject_AndAuditFailureRollsBackEntireIdentityMutation()
    {
        var lower = await Role(); await Manager(lower.Id); var item = await Item(lower.Id); var version = await Version();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Roles.UpdateAsync(new(item.Id, "Wrong Stamp", null, true, "forged", version)));
        await using (var db = database.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_reject_audit BEFORE INSERT ON authorization_change_history FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable()");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Roles.UpdateAsync(new(item.Id, "Should Roll Back", null, true, item.ConcurrencyStamp, version)));
            await using var verify = database.CreateDbContext();
            Assert.Equal(item.Name, await verify.Roles.Where(x => x.Id == item.Id).Select(x => x.Name).SingleAsync());
            Assert.Equal(version.Epoch, await verify.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync());
        }
        finally { await using var db = database.CreateDbContext(); await db.Database.ExecuteSqlRawAsync("DROP TRIGGER test_reject_audit ON authorization_change_history"); }
        await Roles.UpdateAsync(new(item.Id, "Committed", null, true, item.ConcurrencyStamp, version));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Roles.UpdateAsync(new(item.Id, "Stale", null, true, item.ConcurrencyStamp, version)));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("username")]
    [InlineData("activation")]
    [InlineData("reset")]
    [InlineData("delete")]
    public async Task PrivilegedTargetChangesRespectUnrelatedAndDormantEnvelopes(string action)
    {
        var dormant = await Role([Permissions.Parts.Create], false); var target = await User(dormant.Id);
        await using (var db = database.CreateDbContext()) await db.Users.Where(x => x.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Manager(); var edit = (await Users.GetUserForEditAsync(target.Id))!;
        if (action == "email") edit.Email = "takeover@example.test";
        if (action == "username") edit.Username = "takeover";
        if (action == "activation") edit.IsActive = true;
        Func<Task> operation = action switch { "reset" => async () => { await Users.GeneratePasswordResetTokenAsync(target.Id, edit.Version, edit.ConcurrencyStamp); },
            "delete" => async () => { await Users.DeleteUserAsync(target.Id, edit.Version, edit.ConcurrencyStamp); }, _ => async () => { await Users.UpdateUserAsync(edit); } };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(operation);
        await using var verify = database.CreateDbContext(); Assert.False(await verify.Users.Where(x => x.Id == target.Id).Select(x => x.IsActive).SingleAsync());
    }

    [Fact]
    public async Task CreateReadUpdateManageRolesAndResetAreIndependentServicePermissions()
    {
        var createRole = await Role([Permissions.Users.Read, Permissions.Users.Create]); var actor = await User(createRole.Id); await session.SignInAsync(actor.Id);
        var input = new CreateUserInput { Username = "create.only", Email = "create.only@example.test", DisplayName = "Create Only", Version = await Version() };
        Assert.True((await Users.CreateUserAsync(input)).Succeeded);
        var target = await User(); var edit = (await Users.GetUserForEditAsync(target.Id))!;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.GeneratePasswordResetTokenAsync(target.Id, edit.Version, edit.ConcurrencyStamp));
        edit.DisplayName = "Not Allowed"; await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.UpdateUserAsync(edit));
        input.Username = "create.extra"; input.Email = "create.extra@example.test"; input.Roles = [createRole.Name!]; input.Version = await Version();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.CreateUserAsync(input));
        await using var db = database.CreateDbContext();
        var createdId = await db.Users.Where(x => x.UserName == "create.only").Select(x => x.Id).SingleAsync();
        Assert.Equal([AppRoles.ReadOnlyId], await db.UserRoles.Where(x => x.UserId == createdId).Select(x => x.RoleId).ToListAsync());
        Assert.False(await db.Users.AnyAsync(x => x.UserName == "create.extra"));
    }

    [Theory]
    [InlineData("display")]
    [InlineData("email")]
    [InlineData("active")]
    [InlineData("roles")]
    [InlineData("reset")]
    [InlineData("delete")]
    public async Task RootTargetIsProtectedEvenFromRoot(string action)
    {
        var edit = (await Users.GetUserForEditAsync(rootId))!; Assert.True(edit.IsProtected); Assert.Empty(edit.Email); Assert.False(edit.CanManageAccount);
        if (action == "display") edit.DisplayName = "Changed";
        if (action == "email") edit.Email = "changed@example.test";
        if (action == "active") edit.IsActive = false;
        if (action == "roles") edit.Roles.Add(AppRoles.Administrator);
        Func<Task> operation = action switch { "reset" => async () => { await Users.GeneratePasswordResetTokenAsync(rootId, edit.Version, edit.ConcurrencyStamp); },
            "delete" => async () => { await Users.DeleteUserAsync(rootId, edit.Version, edit.ConcurrencyStamp); }, _ => async () => { await Users.UpdateUserAsync(edit); } };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(operation);
    }

    [Fact]
    public async Task PendingRootDeniesNewAdministration_WithoutChangingLegacyBusinessReads()
    {
        await database.ResetAsync(); var actor = await User(AppRoles.AdministratorId); await session.SignInAsync(actor.Id);
        Assert.Equal(AuthorizationDenial.PendingRoot, (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.GetUsersAsync())).Denial);
        Assert.Equal(AuthorizationDenial.PendingRoot, (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.GetRolesAsync())).Denial);
        Assert.Empty(await new Confast.Web.Features.Customers.CustomerService(database).GetCustomersAsync());
    }

    [Fact]
    public async Task AuthorizedResetPersistsTrustedIssuer_AndConsumesOnceWithAtomicCredentialChange()
    {
        var actor = await User(AppRoles.AdministratorId); var target = await User(AppRoles.QualityId); await session.SignInAsync(actor.Id);
        var edit = (await Users.GetUserForEditAsync(target.Id))!;
        var before = Evaluator.ComputationCount;
        var issued = await Users.GeneratePasswordResetTokenAsync(target.Id, edit.Version, edit.ConcurrencyStamp);
        Assert.Equal(before + 1, Evaluator.ComputationCount); Assert.NotNull(issued.Token);
        await using (var db = database.CreateDbContext())
        {
            var delegation = Assert.Single(await db.PasswordResetDelegations.ToListAsync());
            Assert.Equal(actor.Id, delegation.IssuerUserId); Assert.Equal(target.Id, delegation.TargetUserId);
            Assert.Equal(actor.SecurityStamp, delegation.IssuerSecurityStamp); Assert.NotEqual(issued.Token, delegation.TokenDigest); Assert.Equal(64, delegation.TokenDigest.Length);
        }
        session.Principal = new(new System.Security.Claims.ClaimsIdentity());
        before = Evaluator.ComputationCount;
        Assert.True((await Users.RedeemPasswordResetAsync(target.Id, issued.Token, "Reset-Accepted-Password-9!")).Succeeded);
        Assert.Equal(before + 1, Evaluator.ComputationCount);
        Assert.False((await Users.RedeemPasswordResetAsync(target.Id, issued.Token, "Reset-Again-Password-9!")).Succeeded);
        await using var verify = database.CreateDbContext();
        var changed = await verify.Users.SingleAsync(x => x.Id == target.Id);
        Assert.NotEqual(target.PasswordHash, changed.PasswordHash); Assert.NotEqual(target.SecurityStamp, changed.SecurityStamp);
        Assert.NotNull((await verify.PasswordResetDelegations.SingleAsync()).ConsumedAtUtc);
        Assert.Equal(2, await verify.AuthorizationChangeHistory.CountAsync(x => x.Purpose.StartsWith("Users.ResetPasswords")));
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("inactive")]
    [InlineData("stamp")]
    [InlineData("disabled")]
    [InlineData("higher target")]
    [InlineData("unrelated target")]
    [InlineData("expired")]
    [InlineData("forged")]
    [InlineData("wrong target")]
    public async Task OutstandingResetRejectsRevokedIssuerEscalatedTargetAndInvalidDelegation(string change)
    {
        var lower = await Role(); var actor = await Manager(lower.Id); var target = await User(lower.Id);
        var edit = (await Users.GetUserForEditAsync(target.Id))!;
        var issued = await Users.GeneratePasswordResetTokenAsync(target.Id, edit.Version, edit.ConcurrencyStamp);
        await using (var db = database.CreateDbContext())
        {
            var anchor = await db.UserRoles.Where(x => x.UserId == actor.Id && x.RoleId != AppRoles.ReadOnlyId).Select(x => x.RoleId).SingleAsync();
            if (change == "revoke") await db.RolePermissions.Where(x => x.RoleId == anchor && x.PermissionKey == Permissions.Users.ResetPasswords).ExecuteDeleteAsync();
            if (change == "inactive") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            if (change == "stamp") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "rotated"));
            if (change == "disabled") await db.Roles.Where(x => x.Id == anchor).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
            if (change is "higher target" or "unrelated target")
            { db.UserRoles.Add(new() { UserId = target.Id, RoleId = change == "higher target" ? AppRoles.AdministratorId : (await Role(Management)).Id }); await db.SaveChangesAsync(); }
        }
        if (change == "expired") clock.Advance(TimeSpan.FromHours(25));
        var result = await Users.RedeemPasswordResetAsync(change == "wrong target" ? actor.Id : target.Id,
            change == "forged" ? issued.Token + "forged" : issued.Token, "Rejected-Password-Change-9!");
        Assert.False(result.Succeeded);
        await using var verify = database.CreateDbContext(); Assert.Equal(target.PasswordHash, await verify.Users.Where(x => x.Id == target.Id).Select(x => x.PasswordHash).SingleAsync());
        Assert.Null((await verify.PasswordResetDelegations.SingleAsync()).ConsumedAtUtc);
    }

    [Fact]
    public async Task UntrackedIdentityTokensAndFailedPasswordsDoNotConsumeOrMutateAnything()
    {
        var target = await User(); var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var token = await manager.GeneratePasswordResetTokenAsync(target);
        Assert.False((await Users.RedeemPasswordResetAsync(target.Id, token, "Untracked-Rejected-9!")).Succeeded);
        var edit = (await Users.GetUserForEditAsync(target.Id))!;
        var issued = await Users.GeneratePasswordResetTokenAsync(target.Id, edit.Version, edit.ConcurrencyStamp);
        var version = await Version();
        Assert.False((await Users.RedeemPasswordResetAsync(target.Id, issued.Token, "weak")).Succeeded);
        Assert.Equal(version, await Version());
        await using var verify = database.CreateDbContext(); Assert.Null((await verify.PasswordResetDelegations.SingleAsync()).ConsumedAtUtc);
        Assert.Equal(target.PasswordHash, await verify.Users.Where(x => x.Id == target.Id).Select(x => x.PasswordHash).SingleAsync());
    }

    [Fact]
    public async Task ConcurrentRedemptionsCannotConsumeTheSameDelegationTwice()
    {
        var target = await User(); var edit = (await Users.GetUserForEditAsync(target.Id))!;
        var token = (await Users.GeneratePasswordResetTokenAsync(target.Id, edit.Version, edit.ConcurrencyStamp)).Token;
        async Task<bool> Redeem(string password)
        { await using var scope = services.CreateAsyncScope(); return (await scope.ServiceProvider.GetRequiredService<UserAdministrationService>().RedeemPasswordResetAsync(target.Id, token, password)).Succeeded; }
        var results = await Task.WhenAll(Redeem("Concurrent-Password-One-9!"), Redeem("Concurrent-Password-Two-9!"));
        Assert.Single(results, x => x);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan time) => now += time;
    }
}
