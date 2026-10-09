using System.Security.Claims;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AuthorizationFoundationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly PermissionCache cache = new();
    private readonly TestAuthenticationState authentication = new();
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() { cache.Dispose(); return Task.CompletedTask; }

    private EffectivePermissionService Evaluator(IDbContextFactory<AppDbContext>? factory = null) => new(
        factory ?? database, authentication, Options.Create(new IdentityOptions()), cache,
        NullLogger<EffectivePermissionService>.Instance);
    private ApplicationAuthorization Guard(EffectivePermissionService evaluator, ClaimsPrincipal? httpActor = null) => new(
        evaluator, new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = httpActor ?? authentication.Principal } });

    private ServiceProvider IdentityServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddAuthentication();
        services.AddDataProtection();
        services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(database.ConnectionString));
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager<ApplicationSignInManager>().AddDefaultTokenProviders();
        services.AddSingleton<AuthenticationStateProvider>(authentication);
        services.AddConfastAuthorization();
        services.AddScoped<UserAdministrationService>();
        return services.BuildServiceProvider();
    }

    private async Task<ApplicationUser> UserAsync(string? roleId = null, bool credentials = true)
    {
        var id = Guid.NewGuid().ToString();
        var user = new ApplicationUser
        {
            Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), DisplayName = "Foundation test",
            Email = id + "@example.test", NormalizedEmail = (id + "@example.test").ToUpperInvariant(),
            IsActive = true, SecurityStamp = Guid.NewGuid().ToString(),
            PasswordHash = credentials ? new PasswordHasher<ApplicationUser>().HashPassword(null!, "Foundation-Test-Password-9!") : null
        };
        await using var db = database.CreateDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        if (roleId is not null)
        {
            db.UserRoles.Add(new() { UserId = id, RoleId = roleId });
            await db.SaveChangesAsync();
        }
        return user;
    }

    private static ClaimsPrincipal Principal(ApplicationUser user, string? stamp = null, string? claimedRole = null) => new(
        new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, stamp ?? user.SecurityStamp!),
            new Claim(ClaimTypes.Role, claimedRole ?? "UntrustedClaim")
        }, IdentityConstants.ApplicationScheme));

    private async Task ProvisionAsync(string accountId, string? excludedId = null)
    {
        await using var db = database.CreateDbContext();
        var epoch = await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT provision_confast_root({accountId}, {epoch}, {"Foundation test provisioning"}, {excludedId})");
    }

    private async Task<ApplicationUser> ReadyAsync()
    {
        var root = await UserAsync();
        await ProvisionAsync(root.Id);
        return root;
    }

    [Fact]
    public async Task Migration_BackfillsLegacyUsersAndPreservesCredentialsIdsAndAssignments()
    {
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync("20261009121947_AddHiddenDirectConversations");
            var id = Guid.NewGuid().ToString();
            var hash = new PasswordHasher<ApplicationUser>().HashPassword(null!, "Migration-Preservation-9!");
            db.Users.Add(new ApplicationUser { Id = id, UserName = id, DisplayName = "Legacy", IsActive = true,
                PasswordHash = hash, SecurityStamp = "legacy-stamp" });
            db.UserRoles.Add(new() { UserId = id, RoleId = AppRoles.QualityId });
            await db.SaveChangesAsync();
            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();
            var preserved = await db.Users.SingleAsync(x => x.Id == id);
            Assert.Equal(hash, preserved.PasswordHash);
            Assert.Equal("legacy-stamp", preserved.SecurityStamp);
            Assert.Equal(new[] { AppRoles.ReadOnlyId, AppRoles.QualityId }.Order(),
                await db.UserRoles.Where(x => x.UserId == id).OrderBy(x => x.RoleId).Select(x => x.RoleId).ToArrayAsync());
            Assert.All(new[] { AppRoles.ReadOnlyId, AppRoles.QualityId, AppRoles.ProductionId, AppRoles.AdministratorId },
                roleId => Assert.Contains(roleId, db.Roles.Select(x => x.Id)));
            Assert.Null((await db.AuthorizationState.SingleAsync()).RootUserId);
        }
        finally { await migrator.MigrateAsync(); }
    }

    [Fact]
    public async Task ConcurrentHumanCreation_AlwaysCommitsExactlyOneBaselineMembership()
    {
        var users = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => UserAsync()));
        await using var db = database.CreateDbContext();
        foreach (var user in users)
            Assert.Equal(1, await db.UserRoles.CountAsync(x => x.UserId == user.Id && x.RoleId == AppRoles.ReadOnlyId));
    }

    [Fact]
    public async Task LegacyUserEditor_CannotChangeDesignatedRootOrIssueResetTokens()
    {
        var root = await ReadyAsync();
        var actor = await UserAsync(AppRoles.AdministratorId);
        authentication.Principal = Principal(actor);
        await using var services = IdentityServices();
        var administration = services.GetRequiredService<UserAdministrationService>();
        var input = await administration.GetUserForEditAsync(root.Id);
        input!.DisplayName = "Changed";
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => administration.UpdateUserAsync(input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => administration.GeneratePasswordResetTokenAsync(root.Id, input.Version, input.ConcurrencyStamp));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => administration.DeleteUserAsync(root.Id, input.Version, input.ConcurrencyStamp));
    }

    [Fact]
    public async Task UserCreation_RejectsAnUnavailableInitialRoleWithoutPartialCreation()
    {
        var root = await ReadyAsync();
        authentication.Principal = Principal(root);
        await using (var db = database.CreateDbContext())
        {
            await db.RoleInheritance.Where(x => x.ParentRoleId == AppRoles.QualityId).ExecuteDeleteAsync();
            await db.RolePermissions.Where(x => x.RoleId == AppRoles.QualityId).ExecuteDeleteAsync();
            await db.Roles.Where(x => x.Id == AppRoles.QualityId).ExecuteDeleteAsync();
        }
        await using var services = IdentityServices();
        var version = (await Evaluator().EvaluateCurrentAsync()).Version;
        await Assert.ThrowsAsync<ArgumentException>(() => services.GetRequiredService<UserAdministrationService>().CreateUserAsync(
            new CreateUserInput { Username = "failed.initialization", Email = "failed@example.test", DisplayName = "Failure",
                Roles = [AppRoles.Quality], Version = version }));
        await using var verify = database.CreateDbContext();
        Assert.False(await verify.Users.AnyAsync(x => x.UserName == "failed.initialization"));
        Assert.DoesNotContain(await verify.UserRoles.ToListAsync(), x => x.UserId != root.Id);
    }

    [Fact]
    public async Task RootProvisioning_IsNotExecutableByPublicDatabasePrincipals()
    {
        await using var db = database.CreateDbContext();
        Assert.False(await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (SELECT FROM pg_proc p, LATERAL aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                WHERE p.oid = 'provision_confast_root(text,bigint,text,text)'::regprocedure
                    AND a.grantee = 0 AND a.privilege_type = 'EXECUTE') AS "Value"
            """).SingleAsync());
    }

    [Fact]
    public async Task ReadyRoot_CannotLoseMembershipCredentialsActiveStateOrDesignation()
    {
        var root = await ReadyAsync();
        var ordinary = await UserAsync();
        await using var db = database.CreateDbContext();
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE identity_users SET is_active = false WHERE id = {root.Id}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE identity_users SET password_hash = NULL WHERE id = {root.Id}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM identity_user_roles WHERE user_id = {root.Id} AND role_id = {AppRoles.RootId}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO identity_user_roles (user_id,role_id) VALUES ({ordinary.Id}, {AppRoles.RootId})"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE authorization_state SET root_user_id = {ordinary.Id} WHERE id = 1"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE authorization_state SET readiness = 0, root_user_id = NULL"));
        Assert.Equal(root.Id, (await db.AuthorizationState.SingleAsync()).RootUserId);
        Assert.True((await db.Users.SingleAsync(x => x.Id == root.Id)).IsActive);
    }

    [Fact]
    public async Task InstallationGenerationChange_PreventsBackupEpochCacheReuse()
    {
        var root = await ReadyAsync();
        authentication.Principal = Principal(root);
        var evaluator = Evaluator();
        var before = await evaluator.EvaluateCurrentAsync();
        await using var db = database.CreateDbContext();
        await db.AuthorizationState.ExecuteUpdateAsync(s => s.SetProperty(x => x.InstallationGeneration, Guid.NewGuid()));
        var after = await evaluator.EvaluateCurrentAsync();
        Assert.NotEqual(before.Version.Generation, after.Version.Generation);
        Assert.Equal(2, evaluator.ComputationCount);
        Assert.Equal(0, evaluator.CacheHitCount);
    }

    [Fact]
    public async Task MutationLock_RejectsStaleVersionAndAuditSharesActualMutationTransaction()
    {
        var root = await ReadyAsync();
        authentication.Principal = Principal(root);
        var guard = Guard(Evaluator());
        using var operation = await guard.BeginAsync("Test.GrantRemoval", new("Role", AppRoles.QualityId), [Permissions.Roles.ManagePermissions]);
        await using var db = database.CreateDbContext();
        await using (var rollback = await db.Database.BeginTransactionAsync())
        {
            await AuthorizationMutationLock.AcquireAsync(db, operation.Snapshot.Version, default);
            await db.RolePermissions.Where(x => x.RoleId == AppRoles.QualityId && x.PermissionKey == Permissions.Inspections.Create).ExecuteDeleteAsync();
            await AuthorizationAudit.RecordAsync(db, operation, operation.Snapshot.Version.Epoch, "Atomic rollback test",
                [new() { Kind = AuthorizationChangeKind.Grant, RoleId = AppRoles.QualityId, PermissionKey = Permissions.Inspections.Create,
                    WasPresent = true, IsPresent = false }], TimeProvider.System, default);
            await rollback.RollbackAsync();
        }
        Assert.True(await db.RolePermissions.AnyAsync(x => x.RoleId == AppRoles.QualityId && x.PermissionKey == Permissions.Inspections.Create));
        Assert.False(await db.AuthorizationChangeHistory.AnyAsync(x => x.OperationId == operation.Id));
        db.ChangeTracker.Clear();
        using var committed = await guard.BeginAsync("Test.GrantRemoval", new("Role", AppRoles.QualityId), [Permissions.Roles.ManagePermissions]);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await AuthorizationMutationLock.AcquireAsync(db, committed.Snapshot.Version, default);
            await db.RolePermissions.Where(x => x.RoleId == AppRoles.QualityId && x.PermissionKey == Permissions.Inspections.Create).ExecuteDeleteAsync();
            await AuthorizationAudit.RecordAsync(db, committed, committed.Snapshot.Version.Epoch, "Atomic commit test",
                [new() { Kind = AuthorizationChangeKind.Grant, RoleId = AppRoles.QualityId, PermissionKey = Permissions.Inspections.Create,
                    WasPresent = true, IsPresent = false }], TimeProvider.System, default);
            await transaction.CommitAsync();
        }
        var history = await db.AuthorizationChangeHistory.Include(x => x.Details).SingleAsync(x => x.OperationId == committed.Id);
        Assert.Equal(root.Id, history.ActorUserId);
        Assert.Equal(AuthorizationActorKind.Human, history.ActorKind);
        Assert.Equal(Permissions.Inspections.Create, Assert.Single(history.Details).PermissionKey);
        await using var stale = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => AuthorizationMutationLock.AcquireAsync(db, operation.Snapshot.Version, default));
        await stale.RollbackAsync();
    }

    [Fact]
    public async Task CatalogVersionMismatch_DeniesCachedRoot()
    {
        var root = await ReadyAsync();
        authentication.Principal = Principal(root);
        var evaluator = Evaluator();
        await evaluator.EvaluateCurrentAsync();
        await using var db = database.CreateDbContext();
        await db.AuthorizationState.ExecuteUpdateAsync(s => s.SetProperty(x => x.CatalogVersion, "wrong-deployment"));
        Assert.Equal(AuthorizationDenial.CatalogMismatch,
            (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => evaluator.EvaluateCurrentAsync())).Denial);
    }

    [Fact]
    public async Task TransitiveDatabaseCycle_IsRejected()
    {
        await using var db = database.CreateDbContext();
        db.Roles.AddRange(new ApplicationRole("A") { Id = "a" }, new ApplicationRole("B") { Id = "b" }, new ApplicationRole("C") { Id = "c" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO role_inheritance VALUES ('a','b'),('b','c')");
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO role_inheritance VALUES ('c','a')"))).SqlState);
    }

    [Fact]
    public async Task SeedsAndPendingState_ArePersistedWithoutSelectingRoot()
    {
        await using var db = database.CreateDbContext();
        var state = await db.AuthorizationState.SingleAsync();
        Assert.Null(state.RootUserId);
        Assert.Equal(AuthorizationReadiness.PendingRoot, state.Readiness);
        Assert.Equal(PermissionCatalog.Version, state.CatalogVersion);
        Assert.NotEqual(Guid.Empty, state.InstallationGeneration);
        Assert.Equal(164, await db.Permissions.CountAsync());
        Assert.Equal(203, await db.RolePermissions.CountAsync());
        Assert.Equal(2, await db.RoleInheritance.CountAsync());
        Assert.Equal(AppRoles.Seeds.Select(x => x.Id).Order(), await db.Roles.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
        var admin = await UserAsync(AppRoles.AdministratorId);
        authentication.Principal = Principal(admin, claimedRole: AppRoles.Root);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Evaluator().EvaluateCurrentAsync());
        Assert.Equal(AuthorizationDenial.PendingRoot, denied.Denial);
    }

    [Theory]
    [InlineData(AppRoles.ReadOnlyId, 16)]
    [InlineData(AppRoles.QualityId, 85)]
    [InlineData(AppRoles.ProductionId, 77)]
    [InlineData(AppRoles.AdministratorId, 163)]
    public async Task ReadyOrdinaryActors_HaveExactEffectiveSeedCounts(string role, int count)
    {
        await ReadyAsync();
        var actor = await UserAsync(role == AppRoles.ReadOnlyId ? null : role);
        authentication.Principal = Principal(actor, claimedRole: AppRoles.Root);
        var snapshot = await Evaluator().EvaluateCurrentAsync();
        Assert.Equal(count, snapshot.Permissions.Count);
        Assert.False(snapshot.IsRoot);
        Assert.False(snapshot.Has(Permissions.Authorization.ManageSecurity));
    }

    [Fact]
    public async Task ExplicitRoot_HasAllKnownKeys_UnknownKeysStillDeny_AndHistoryIsAtomic()
    {
        var root = await ReadyAsync();
        authentication.Principal = Principal(root);
        var evaluator = Evaluator();
        var snapshot = await evaluator.EvaluateCurrentAsync();
        Assert.True(snapshot.IsRoot);
        Assert.Equal(164, snapshot.Permissions.Count);
        Assert.False(snapshot.Has("Unknown.Permission"));
        Assert.Equal(AuthorizationDenial.UnknownPermission,
            (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Guard(evaluator).BeginAsync(
                "Test", new("Root"), ["Unknown.Permission"]))).Denial);
        await using var db = database.CreateDbContext();
        Assert.False(await db.UserRoles.AnyAsync(x => x.UserId == root.Id && x.RoleId == AppRoles.AdministratorId));
        var history = await db.AuthorizationChangeHistory.Include(x => x.Details).SingleAsync();
        Assert.Equal(AuthorizationActorKind.InstallationOperator, history.ActorKind);
        Assert.Null(history.ActorUserId);
        Assert.Equal(root.Id, Assert.Single(history.Details).UserId);
        Assert.Equal(snapshot.Version.Epoch, history.ResultingEpoch);
        Assert.True(history.ResultingEpoch > history.PreviousEpoch);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM authorization_change_details"));
    }

    [Fact]
    public async Task Provisioning_RejectsUninitializedBrowserTestStaleAndRepeatedRequests()
    {
        var uninitialized = await UserAsync(credentials: false);
        await Assert.ThrowsAsync<PostgresException>(() => ProvisionAsync(uninitialized.Id));
        var browser = await UserAsync();
        await Assert.ThrowsAsync<PostgresException>(() => ProvisionAsync(browser.Id, browser.Id));
        await using var db = database.CreateDbContext();
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT provision_confast_root({browser.Id}, {-1L}, {"Stale"}, {"excluded"})"));
        Assert.Empty(await db.AuthorizationChangeHistory.ToListAsync());
        var root = await ReadyAsync();
        await Assert.ThrowsAsync<PostgresException>(() => ProvisionAsync(root.Id));
    }

    [Fact]
    public async Task BaselineEnrollment_IsAutomaticIdempotentAndCannotBeRemoved()
    {
        var actor = await UserAsync(AppRoles.QualityId);
        await using var db = database.CreateDbContext();
        Assert.Equal(2, await db.UserRoles.CountAsync(x => x.UserId == actor.Id));
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO identity_user_roles (user_id, role_id) VALUES ({actor.Id}, {AppRoles.ReadOnlyId}) ON CONFLICT DO NOTHING");
        Assert.Equal(2, await db.UserRoles.CountAsync(x => x.UserId == actor.Id));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM identity_user_roles WHERE user_id = {actor.Id} AND role_id = {AppRoles.ReadOnlyId}"));
        Assert.Equal(2, await db.UserRoles.CountAsync(x => x.UserId == actor.Id));
        await db.Users.Where(x => x.Id == actor.Id).ExecuteDeleteAsync();
        Assert.False(await db.UserRoles.AnyAsync(x => x.UserId == actor.Id));
    }

    [Theory]
    [InlineData("INSERT INTO role_permissions VALUES ('47cd3d4a-0d66-4acf-8556-4017336798d8','Unknown.Key')")]
    [InlineData("INSERT INTO role_permissions VALUES ('47cd3d4a-0d66-4acf-8556-4017336798d8','Authorization.ManageSecurity')")]
    [InlineData("INSERT INTO role_permissions VALUES ('e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6','Users.Read')")]
    [InlineData("INSERT INTO role_permissions VALUES ('1b171cb9-9273-42fc-b790-ea934dbb12b9','Chat.Access')")]
    [InlineData("UPDATE identity_roles SET is_enabled = false WHERE system_kind <> 0")]
    [InlineData("UPDATE identity_roles SET name = 'Renamed' WHERE system_kind <> 0")]
    [InlineData("UPDATE identity_roles SET system_kind = 0, system_key = NULL WHERE system_kind <> 0")]
    [InlineData("DELETE FROM identity_roles WHERE system_kind <> 0")]
    [InlineData("DELETE FROM authorization_state")]
    [InlineData("UPDATE permissions SET authority = 0")]
    [InlineData("INSERT INTO role_inheritance VALUES ('1b171cb9-9273-42fc-b790-ea934dbb12b9','47cd3d4a-0d66-4acf-8556-4017336798d8')")]
    [InlineData("INSERT INTO role_inheritance VALUES ('47cd3d4a-0d66-4acf-8556-4017336798d8','e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6')")]
    [InlineData("INSERT INTO role_inheritance VALUES ('47cd3d4a-0d66-4acf-8556-4017336798d8','47cd3d4a-0d66-4acf-8556-4017336798d8')")]
    [InlineData("TRUNCATE authorization_state CASCADE")]
    [InlineData("TRUNCATE identity_roles CASCADE")]
    public async Task StructuralBackstops_RejectInvalidWrites(string sql)
    {
        await using var db = database.CreateDbContext();
        var before = await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(before, await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync());
    }

    [Fact]
    public async Task Constraints_RejectDuplicateGrantsEdgesAndMissingRoles()
    {
        await using var db = database.CreateDbContext();
        foreach (var sql in new[]
        {
            "INSERT INTO role_permissions VALUES ('1b171cb9-9273-42fc-b790-ea934dbb12b9','Parts.Read')",
            "INSERT INTO role_inheritance VALUES ('47cd3d4a-0d66-4acf-8556-4017336798d8','9eb9ef78-7737-47a5-89fc-10513d3e9c1b')"
        }) Assert.Equal(PostgresErrorCodes.UniqueViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO role_permissions VALUES ('missing','Parts.Read')"))).SqlState);
    }

    [Fact]
    public async Task ConcurrentOpposingEdges_AreSerializedAndCannotCreateCycle()
    {
        await using var db = database.CreateDbContext();
        db.Roles.AddRange(new ApplicationRole("First") { Id = "first", NormalizedName = "FIRST" },
            new ApplicationRole("Second") { Id = "second", NormalizedName = "SECOND" });
        await db.SaveChangesAsync();
        await using var first = new NpgsqlConnection(database.ConnectionString);
        await using var second = new NpgsqlConnection(database.ConnectionString);
        await first.OpenAsync(); await second.OpenAsync();
        await using var tx1 = await first.BeginTransactionAsync();
        await using var tx2 = await second.BeginTransactionAsync();
        await using var addFirst = new NpgsqlCommand("INSERT INTO role_inheritance VALUES ('first','second')", first, tx1);
        await addFirst.ExecuteNonQueryAsync();
        await using var addSecond = new NpgsqlCommand("INSERT INTO role_inheritance VALUES ('second','first')", second, tx2);
        var competing = addSecond.ExecuteNonQueryAsync();
        await tx1.CommitAsync();
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(async () => await competing)).SqlState);
        await tx2.RollbackAsync();
        Assert.Equal(1, await db.RoleInheritance.CountAsync(x => x.ChildRoleId == "first" || x.ChildRoleId == "second"));
    }

    [Fact]
    public async Task EpochChanges_AreTransactionalAndProfilesDoNotInvalidateGrants()
    {
        var actor = await UserAsync();
        await using var db = database.CreateDbContext();
        var before = await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync();
        await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DisplayName, "New display"));
        Assert.Equal(before, await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync());
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE identity_roles SET is_enabled = false WHERE system_kind = 0");
            Assert.True(await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync() > before);
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync());
        Assert.All(await db.Roles.ToListAsync(), x => Assert.True(x.IsEnabled));
    }

    [Fact]
    public async Task LogicalOperation_ComposesOncePoliciesReuseFrameAndSeparateEventsAreFresh()
    {
        await ReadyAsync();
        var actor = await UserAsync(AppRoles.QualityId);
        authentication.Principal = Principal(actor);
        var evaluator = Evaluator();
        var scope = new AuthorizationScope("Inspection", "123");
        using var operation = await Guard(evaluator).BeginAsync("Receive", scope, [Permissions.Receiving.BeginInspection]);
        operation.Require("Receive", scope, Permissions.Inspections.Create);
        operation.Require("Receive", scope, Permissions.Parts.Read);
        Assert.Equal(1, evaluator.SnapshotCount);
        Assert.Equal(1, evaluator.ComputationCount);
        Assert.Equal(AuthorizationDenial.WrongOperation,
            Assert.Throws<AuthorizationDeniedException>(() => operation.Require("Other", scope, Permissions.Parts.Read)).Denial);
        Assert.Throws<AuthorizationDeniedException>(() => operation.Require("Receive", new("Inspection", "456"), Permissions.Parts.Read));
        var handler = new PermissionAuthorizationHandler(evaluator, Options.Create(new IdentityOptions()));
        var requirement = new PermissionRequirement(Permissions.Inspections.Create);
        var context = new AuthorizationHandlerContext([requirement], authentication.Principal, operation);
        await handler.HandleAsync(context);
        Assert.True(context.HasSucceeded);
        Assert.Equal(1, evaluator.SnapshotCount);
        using var next = await Guard(evaluator).BeginAsync("Receive", scope, [Permissions.Receiving.BeginInspection]);
        Assert.NotEqual(operation.Id, next.Id);
        Assert.Equal(2, evaluator.SnapshotCount);
        Assert.Equal(1, evaluator.ComputationCount);
        Assert.Equal(1, evaluator.CacheHitCount);
        operation.Dispose();
        Assert.Throws<AuthorizationDeniedException>(() => operation.Require("Receive", scope, Permissions.Parts.Read));
    }

    [Fact]
    public async Task Revocation_VersionMismatchAndDisabledRolesTakeEffectOnNextOperation()
    {
        await ReadyAsync();
        var actor = await UserAsync(AppRoles.QualityId);
        authentication.Principal = Principal(actor);
        var evaluator = Evaluator();
        var guard = Guard(evaluator);
        using var first = await guard.BeginAsync("Create", new("Inspection"), [Permissions.Inspections.Create]);
        await using var db = database.CreateDbContext();
        await db.RolePermissions.Where(x => x.RoleId == AppRoles.QualityId && x.PermissionKey == Permissions.Inspections.Create).ExecuteDeleteAsync();
        Assert.Equal(AuthorizationDenial.MissingPermission,
            (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => guard.BeginAsync("Create", new("Inspection"), [Permissions.Inspections.Create]))).Denial);
        var second = await evaluator.EvaluateCurrentAsync();
        Assert.True(second.Version.Epoch > first.Snapshot.Version.Epoch);
        Assert.False(second.Has(Permissions.Inspections.Create));
        // Already authorized work retains its initial snapshot, as specified.
        first.Require("Create", new("Inspection"), Permissions.Inspections.Create);
        await db.Roles.Where(x => x.Id == AppRoles.QualityId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
        Assert.Equal(16, (await evaluator.EvaluateCurrentAsync()).Permissions.Count);
        await db.Roles.Where(x => x.Id == AppRoles.QualityId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, true));
        Assert.Equal(84, (await evaluator.EvaluateCurrentAsync()).Permissions.Count);
    }

    [Fact]
    public async Task ActiveAndSecurityStamp_AreCheckedEvenOnCacheHit()
    {
        await ReadyAsync();
        var actor = await UserAsync(AppRoles.QualityId);
        authentication.Principal = Principal(actor);
        var evaluator = Evaluator();
        await evaluator.EvaluateCurrentAsync();
        await using var db = database.CreateDbContext();
        await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        Assert.Equal(AuthorizationDenial.InactiveAccount,
            (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => evaluator.EvaluateCurrentAsync())).Denial);
        await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true).SetProperty(x => x.SecurityStamp, "changed"));
        Assert.Equal(AuthorizationDenial.InvalidSession,
            (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => evaluator.EvaluateCurrentAsync())).Denial);
    }

    [Fact]
    public async Task ActorIdentity_CannotBorrowHttpPrincipalOrSpoofRootUsingClaims()
    {
        var root = await ReadyAsync();
        var ordinary = await UserAsync();
        var evaluator = Evaluator();
        var guard = Guard(evaluator, Principal(root));
        Assert.Equal(AuthorizationDenial.Unauthenticated,
            (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => guard.BeginAsync("Test", new("Root"), [Permissions.Authorization.ManageSecurity]))).Denial);
        authentication.Principal = Principal(ordinary, claimedRole: AppRoles.Root);
        Assert.False((await evaluator.EvaluateCurrentAsync()).IsRoot);
        authentication.Principal = Principal(root, stamp: ordinary.SecurityStamp);
        Assert.Equal(AuthorizationDenial.InvalidSession,
            (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => evaluator.EvaluateCurrentAsync())).Denial);
        authentication.Principal = Principal(ordinary);
        using var httpOperation = await guard.BeginHttpAsync("Root", new("Security"), [Permissions.Authorization.ManageSecurity]);
        Assert.Equal(root.Id, httpOperation.ActorUserId);
    }

    [Fact]
    public async Task CorruptReadyRoot_FailsClosedDespiteCachedPermissions()
    {
        var root = await ReadyAsync();
        authentication.Principal = Principal(root);
        var evaluator = Evaluator();
        await evaluator.EvaluateCurrentAsync();
        // Deliberate database OWNER corruption, not a production bypass. Reset restores it.
        await using var db = database.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE identity_user_roles DISABLE TRIGGER USER; DELETE FROM identity_user_roles WHERE role_id = 'e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6'; ALTER TABLE identity_user_roles ENABLE TRIGGER USER");
        await transaction.CommitAsync();
        Assert.Equal(AuthorizationDenial.InvalidState,
            (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => evaluator.EvaluateCurrentAsync())).Denial);
    }

    [Fact]
    public async Task DatabaseFailure_DeniesInsteadOfReusingCache()
    {
        var root = await ReadyAsync();
        authentication.Principal = Principal(root);
        await Evaluator().EvaluateCurrentAsync();
        Assert.Equal(AuthorizationDenial.DatabaseUnavailable,
            (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Evaluator(new FailedFactory()).EvaluateCurrentAsync())).Denial);
    }

    [Fact]
    public async Task KnownPolicies_AreRegisteredWithStandardProvider_UnknownPolicyDoesNotExist()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfastAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        Assert.IsType<DefaultAuthorizationPolicyProvider>(policies);
        Assert.NotNull(await policies.GetPolicyAsync(AuthorizationRegistration.PolicyName(Permissions.Chat.Access)));
        Assert.Null(await policies.GetPolicyAsync(AuthorizationRegistration.PolicyName("Unknown.Key")));
    }

    private sealed class TestAuthenticationState : AuthenticationStateProvider
    {
        internal ClaimsPrincipal Principal { get; set; } = new(new ClaimsIdentity());
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(Principal));
    }
    private sealed class FailedFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new NpgsqlException("Simulated connection failure.");
    }
}
