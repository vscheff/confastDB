using System.Data.Common;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.Identity;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InspectorAuthorizationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly AuthorizationTestSession session = new(database);
    private EffectivePermissionService evaluator = null!;
    private InspectionService inspections = null!;
    private InspectorEligibilityService eligibility = null!;
    private string rootId = "";
    public async Task InitializeAsync()
    {
        await database.ResetAsync(); rootId = await session.ProvisionRootAsync(); await session.SignInAsync(rootId);
        evaluator = session.Evaluator; inspections = new(database, evaluator); eligibility = new(database, evaluator);
    }
    public Task DisposeAsync() { session.Dispose(); return Task.CompletedTask; }
    private async Task<ApplicationUser> User(string name, params string[] roleIds)
    {
        var username = Guid.NewGuid().ToString(); var user = new ApplicationUser { UserName = username, NormalizedUserName = username.ToUpperInvariant(),
            DisplayName = name, SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() };
        await using var db = database.CreateDbContext(); db.Users.Add(user); await db.SaveChangesAsync();
        db.UserRoles.AddRange(roleIds.Select(id => new IdentityUserRole<string> { UserId = user.Id, RoleId = id })); await db.SaveChangesAsync(); return user;
    }
    private async Task<string> Role(string[] keys, params string[] parents)
    {
        var name = Guid.NewGuid().ToString(); var role = new ApplicationRole(name) { NormalizedName = name.ToUpperInvariant() };
        await using var db = database.CreateDbContext(); db.Roles.Add(role); await db.SaveChangesAsync();
        db.RolePermissions.AddRange(keys.Select(k => new RolePermission { RoleId = role.Id, PermissionKey = k }));
        db.RoleInheritance.AddRange(parents.Select(p => new RoleInheritance { ChildRoleId = role.Id, ParentRoleId = p }));
        await db.SaveChangesAsync(); return role.Id;
    }
    private async Task<long> Part(long? gageType = null)
    {
        await using var db = database.CreateDbContext();
        var part = new Part { PartNumber = Guid.NewGuid().ToString(), Customer = new Customer { Name = "Inspector Authorization Test" } };
        var revision = new InspectionCriteriaRevision { Part = part, RevisionNumber = 1, CreatedAtUtc = DateTimeOffset.UtcNow, PublishedAtUtc = DateTimeOffset.UtcNow };
        revision.Criteria.Add(new() { Name = "Length", Minimum = "1", MaximumOrTolerance = "2", DisplayOrder = 1, GageTypeId = gageType });
        db.InspectionCriteriaRevisions.Add(revision); await db.SaveChangesAsync(); return part.Id;
    }
    private static CreateInspectionModel Create(long part, string? inspector = null) => new() { PartId = part,
        InspectorUserId = inspector, QuantityReceived = 100, InspectionDate = DateOnly.FromDateTime(DateTime.Today) };

    [Fact]
    public async Task CandidatesUseActiveEffectiveCreate_IncludingInheritedMultiRoleAdministratorAndRoot()
    {
        var creator = await Role([Permissions.Inspections.Create]); var inherited = await Role([], creator);
        var direct = await User("Direct", creator); var throughParent = await User("Inherited", inherited);
        var multiple = await User("Multiple", AppRoles.ProductionId, creator); var administrator = await User("Administrator", AppRoles.AdministratorId);
        var production = await User("Production", AppRoles.ProductionId); var baseline = await User("Baseline"); var inactive = await User("Inactive", creator);
        await using (var db = database.CreateDbContext()) await db.Users.Where(x => x.Id == inactive.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        var before = evaluator.ComputationCount;
        var candidates = await eligibility.GetCandidatesAsync(); Assert.Equal(before + 1, evaluator.ComputationCount);
        Assert.Equal(new[] { rootId, direct.Id, throughParent.Id, multiple.Id, administrator.Id }.Order(), candidates.Select(x => x.UserId).Order());
        Assert.DoesNotContain(candidates, x => x.UserId == production.Id || x.UserId == baseline.Id || x.UserId == inactive.Id);
        await using (var db = database.CreateDbContext()) await db.RolePermissions.Where(x => x.RoleId == creator).ExecuteDeleteAsync();
        candidates = await eligibility.GetCandidatesAsync();
        Assert.DoesNotContain(candidates, x => x.UserId == direct.Id || x.UserId == throughParent.Id || x.UserId == multiple.Id);
    }

    [Fact]
    public async Task CandidateQueryCountDoesNotGrowWithAccountCount()
    {
        var creator = await Role([Permissions.Inspections.Create]);
        for (var i = 0; i < 20; i++) await User("Inspector " + i, creator);
        var counter = new QueryCounter(); var factory = new CountingFactory(database.ConnectionString, counter);
        using var cache = new PermissionCache(); var evaluation = new EffectivePermissionService(factory, session, Options.Create(new IdentityOptions()), cache,
            NullLogger<EffectivePermissionService>.Instance);
        var candidates = await new InspectorEligibilityService(factory, evaluation).GetCandidatesAsync();
        Assert.Equal(21, candidates.Count); Assert.InRange(counter.Count, 1, 10); Assert.Equal(1, evaluation.ComputationCount);
    }

    [Fact]
    public async Task CreateOnlyActorCanInitiateWithoutRead_UsingOneEvaluationAndSeparateInspectorIdentity()
    {
        var creator = await Role([Permissions.Inspections.Create]); var actor = await User("Creating Actor", creator);
        var attributed = await User("Attributed Inspector", creator); var part = await Part(); await session.SignInAsync(actor.Id);
        Assert.DoesNotContain(Permissions.Inspections.Read, PermissionCatalog.ExpandRequirements([Permissions.Inspections.Create]));
        var snapshot = await evaluator.EvaluateCurrentAsync();
        Assert.All(snapshot.Provenance[Permissions.Inspections.Read], source => Assert.Equal(AppRoles.ReadOnlyId, source.GrantRoleId));
        var before = evaluator.ComputationCount;
        var created = await inspections.CreateInspectionAsync(Create(part, attributed.Id));
        Assert.Equal(InspectionOperationStatus.Succeeded, created.Status); Assert.Equal(before + 1, evaluator.ComputationCount);
        var stored = await inspections.GetInspectionAsync(created.InspectionId!.Value);
        Assert.Equal(attributed.Id, stored!.InspectorUserId); Assert.Equal("Attributed Inspector", stored.Inspector);
        await using var db = database.CreateDbContext(); Assert.False(await db.ContainerReceiptAllocations.AnyAsync());
    }

    [Theory]
    [InlineData("production")]
    [InlineData("inactive actor")]
    [InlineData("stale stamp")]
    [InlineData("pending")]
    [InlineData("unguarded constructor")]
    public async Task ActualCreationBoundaryFailsClosed(string reason)
    {
        var part = await Part(); var actor = await User("Actor", AppRoles.QualityId); await session.SignInAsync(actor.Id);
        if (reason == "production") { actor = await User("Production", AppRoles.ProductionId); await session.SignInAsync(actor.Id); }
        if (reason == "inactive actor") { await using var db = database.CreateDbContext(); await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false)); }
        if (reason == "stale stamp") { await using var db = database.CreateDbContext(); await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "rotated")); }
        if (reason == "pending") { await database.ResetAsync(); actor = await User("Pending", AppRoles.QualityId); await session.SignInAsync(actor.Id); part = await Part(); }
        var service = reason == "unguarded constructor" ? new InspectionService(database) : inspections;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.CreateInspectionAsync(Create(part, actor.Id)));
        await using var verify = database.CreateDbContext(); Assert.Empty(await verify.Inspections.ToListAsync());
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("revoked")]
    [InlineData("unknown")]
    [InlineData("name only")]
    public async Task NewSelectionsRequireStableCurrentlyEligibleUser(string reason)
    {
        var part = await Part(); var user = await User("Inspector", AppRoles.QualityId);
        await using (var db = database.CreateDbContext())
        {
            if (reason == "inactive") await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            if (reason == "revoked") await db.UserRoles.Where(x => x.UserId == user.Id && x.RoleId != AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        }
        var model = Create(part, reason == "unknown" ? "unknown-account" : reason == "name only" ? null : user.Id);
        if (reason == "name only") model.Inspector = user.DisplayName;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.CreateInspectionAsync(model));
        await using var verify = database.CreateDbContext(); Assert.Empty(await verify.Inspections.ToListAsync());
    }

    [Fact]
    public async Task DuplicateNamesUseDistinctIdsAndCalipers_AndSnapshotsSurviveAccountRename()
    {
        long typeId, firstCaliper, secondCaliper;
        await using (var db = database.CreateDbContext())
        {
            var type = new GageType { Name = "Digital Caliper" }; var first = new Gage { GageType = type, GageNumber = "ID-ONE" };
            var second = new Gage { GageType = type, GageNumber = "ID-TWO" }; db.Gages.AddRange(first, second); await db.SaveChangesAsync();
            typeId = type.Id; firstCaliper = first.Id; secondCaliper = second.Id;
        }
        var one = await User("Same Name", AppRoles.QualityId); var two = await User("Same Name", AppRoles.QualityId);
        await using (var db = database.CreateDbContext())
        { await db.Users.Where(x => x.Id == one.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CaliperId, firstCaliper));
          await db.Users.Where(x => x.Id == two.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CaliperId, secondCaliper)); }
        Assert.Equal(2, (await eligibility.GetCandidatesAsync()).Count(x => x.DisplayName == "Same Name"));
        var created = await inspections.CreateInspectionAsync(Create(await Part(typeId), two.Id));
        var edit = (await inspections.GetInspectionAsync(created.InspectionId!.Value))!;
        Assert.Equal(two.Id, edit.InspectorUserId); Assert.Equal(secondCaliper, Assert.Single(edit.Results).GageId);
        await using (var db = database.CreateDbContext()) await db.Users.Where(x => x.Id == two.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DisplayName, "New Name"));
        edit.InspectorNotes = "Preserve history"; Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.SaveInspectionAsync(edit)).Status);
        Assert.Equal("Same Name", (await inspections.GetInspectionAsync(edit.Id))!.Inspector);
    }

    [Fact]
    public async Task UnchangedInactiveHistoricalAttributionSavesWithoutNewPermissionEvaluation()
    {
        var inspector = await User("Historical Name", AppRoles.QualityId);
        var created = await inspections.CreateInspectionAsync(Create(await Part(), inspector.Id));
        await using (var db = database.CreateDbContext())
        { await db.Users.Where(x => x.Id == inspector.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
          await db.UserRoles.Where(x => x.UserId == inspector.Id && x.RoleId != AppRoles.ReadOnlyId).ExecuteDeleteAsync(); }
        session.Principal = new(new System.Security.Claims.ClaimsIdentity());
        var edit = (await inspections.GetInspectionAsync(created.InspectionId!.Value))!; edit.Results[0].ActualMin = "1.1";
        var before = evaluator.ComputationCount; Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.SaveInspectionAsync(edit)).Status);
        Assert.Equal(before, evaluator.ComputationCount); Assert.Equal(inspector.Id, edit.InspectorUserId); Assert.Equal("Historical Name", edit.Inspector);
        await using var verify = database.CreateDbContext();
        verify.Users.Remove(await verify.Users.SingleAsync(x => x.Id == inspector.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
        Assert.NotNull(await inspections.GetInspectionAsync(edit.Id));
    }

    [Fact]
    public async Task ChangedAttributionRequiresUpdateAndFreshQualification_ThenNextAutosaveKeepsSnapshot()
    {
        var creator = await Role([Permissions.Inspections.Create]); var actor = await User("Creator Only", creator);
        var first = await User("First", creator); var second = await User("Second", creator);
        var created = await inspections.CreateInspectionAsync(Create(await Part(), first.Id)); await session.SignInAsync(actor.Id);
        var edit = (await inspections.GetInspectionAsync(created.InspectionId!.Value))!; edit.InspectorUserId = second.Id;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.SaveInspectionAsync(edit));
        await session.SignInAsync(rootId);
        Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.SaveInspectionAsync(edit)).Status);
        Assert.Equal("Second", edit.Inspector); edit.InspectorNotes = "Next autosave";
        Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.SaveInspectionAsync(edit)).Status);
        edit.InspectorUserId = first.Id;
        await using (var db = database.CreateDbContext()) await db.UserRoles.Where(x => x.UserId == first.Id && x.RoleId != AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.SaveInspectionAsync(edit));
        edit = (await inspections.GetInspectionAsync(edit.Id))!; edit.Inspector = "Forged Snapshot";
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.SaveInspectionAsync(edit));
    }

    [Fact]
    public async Task BackfillLinksOnlyUnambiguousNames_AndPreservesOriginalNamesAndCredentials()
    {
        await database.ResetAsync();
        var unique = await User("Unique Inspector"); await User("Duplicated"); await User("Duplicated");
        var part = await Part(); long uniqueInspectionId, ambiguousInspectionId, unknownInspectionId;
        await using (var db = database.CreateDbContext())
        {
            var revision = await db.InspectionCriteriaRevisions.Select(x => x.Id).SingleAsync();
            var records = new[] { "  Unique Inspector  ", "Duplicated", "Unknown" }.Select(name => new Inspection {
                PartId = part, InspectionCriteriaRevisionId = revision, Inspector = name, InspectionDate = DateOnly.FromDateTime(DateTime.Today) }).ToArray();
            db.Inspections.AddRange(records); await db.SaveChangesAsync(); uniqueInspectionId = records[0].Id; ambiguousInspectionId = records[1].Id; unknownInspectionId = records[2].Id;
            var migrator = db.GetService<IMigrator>();
            try { await migrator.MigrateAsync("20261009183927_AddAuthorizationFoundation"); await migrator.MigrateAsync(); }
            finally { await migrator.MigrateAsync(); }
        }
        await using var verify = database.CreateDbContext();
        var linked = await verify.Inspections.SingleAsync(x => x.Id == uniqueInspectionId);
        Assert.Equal(unique.Id, linked.InspectorUserId); Assert.Equal("  Unique Inspector  ", linked.Inspector);
        Assert.Null((await verify.Inspections.SingleAsync(x => x.Id == ambiguousInspectionId)).InspectorUserId);
        Assert.Null((await verify.Inspections.SingleAsync(x => x.Id == unknownInspectionId)).InspectorUserId);
        var after = await verify.Users.SingleAsync(x => x.Id == unique.Id); Assert.Equal(unique.SecurityStamp, after.SecurityStamp); Assert.Equal(unique.Id, after.Id);
        Assert.Equal(AuthorizationReadiness.PendingRoot, await verify.AuthorizationState.Select(x => x.Readiness).SingleAsync());
    }

    [Fact]
    public async Task BorrowedOrClosedOperationCannotAuthorizeReceivingCreation()
    {
        var model = Create(await Part(), rootId);
        using var borrowed = await session.Guard.BeginAsync("Inspections.Create", new("Part", model.PartId.ToString()), [Permissions.Inspections.Create]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.CreateInspectionWithCallbackAsync(model, borrowed, 1, (_, _, _) => Task.FromResult<InspectionOperationResult?>(null)));
        using var closed = await session.Guard.BeginAsync("Receiving.BeginInspection", new("ReceiptLine", "1"), [Permissions.Receiving.BeginInspection, Permissions.Inspections.Create]);
        closed.Dispose();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.CreateInspectionWithCallbackAsync(model, closed, 1, (_, _, _) => Task.FromResult<InspectionOperationResult?>(null)));
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
    }
    private sealed class CountingFactory(string connection, QueryCounter counter) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(counter).Options);
    }
}
