using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    [Theory]
    [InlineData(AppRoles.AdministratorId)]
    [InlineData(AppRoles.ProductionId)]
    [InlineData(AppRoles.QualityId)]
    public async Task ReadOnlyParentCanBePreviewedSavedAndRemovedFromSeededRoles(string roleId)
    {
        var before = await Roles.GetEditorAsync(roleId);
        Assert.True(before.Parents.Single(x => x.Id == AppRoles.ReadOnlyId).CanAdd);
        var baseline = await Roles.GetEditorAsync(AppRoles.ReadOnlyId);
        var baselineKeys = baseline.Matrix.Where(x => x.Effective).Select(x => x.Definition.Key).ToArray();
        var input = (await Proposal(roleId)) with { Parents = before.Role.Parents.Append(AppRoles.ReadOnlyId).ToArray() };

        var preview = await Roles.PreviewAsync(input);
        Assert.True(preview.Permitted, preview.Rejection);
        Assert.All(baselineKeys, key => Assert.True(preview.Matrix.Single(x => x.Definition.Key == key).Effective));
        Assert.True(preview.Matrix.Single(x => x.Definition.Key == Permissions.Parts.Read).Usable);
        await Roles.SaveEditorAsync(input);

        var saved = await Roles.GetEditorAsync(roleId);
        Assert.True(saved.Version.Epoch > before.Version.Epoch);
        Assert.Contains(saved.Parents, x => x.Id == AppRoles.ReadOnlyId && x.Direct);
        Assert.Equal(before.Role.Grants, saved.Role.Grants);
        var read = saved.Matrix.Single(x => x.Definition.Key == Permissions.Parts.Read);
        Assert.True(read.Inherited);
        Assert.Contains(read.Sources, x => x.RoleId == AppRoles.ReadOnlyId && x.IsInherited && x.IsActive);
        await using (var db = database.CreateDbContext())
            Assert.Contains(await db.AuthorizationChangeHistory.Include(x => x.Details).ToArrayAsync(),
                x => x.Purpose == "Roles.Edit" && x.Details.Any(d => d.Kind == AuthorizationChangeKind.Inheritance
                    && d.RoleId == roleId && d.ParentRoleId == AppRoles.ReadOnlyId && d.IsPresent == true));

        // A role's inherited grants must work without also assigning ReadOnly to the account.
        var member = await User(roleId);
        await using (var db = database.CreateDbContext())
            await db.UserRoles.Where(x => x.UserId == member.Id && x.RoleId == AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        await session.SignInAsync(member.Id);
        var access = await Evaluator.EvaluateCurrentAsync();
        Assert.All(baselineKeys, key => Assert.True(access.Has(key)));

        await session.SignInAsync(rootId);
        var item = await Item(roleId);
        await Roles.SetParentsAsync(roleId, before.Role.Parents, item.ConcurrencyStamp, await Version());
        var removed = await Roles.GetEditorAsync(roleId);
        Assert.Equal(before.Matrix.Select(x => (x.Definition.Key, x.Effective)),
            removed.Matrix.Select(x => (x.Definition.Key, x.Effective)));
        await session.SignInAsync(member.Id);
        Assert.Equal(before.Matrix.Single(x => x.Definition.Key == Permissions.Parts.Read).Effective,
            (await Evaluator.EvaluateCurrentAsync()).Has(Permissions.Parts.Read));
    }

    [Fact]
    public async Task DelegatedEditorCanAddReadOnlyWithoutGainingManagementReach()
    {
        var target = await Role();
        await Manager(target.Id);
        var input = (await Proposal(target.Id)) with { Parents = [AppRoles.ReadOnlyId] };
        Assert.True((await Roles.GetEditorAsync(target.Id)).Parents.Single(x => x.Id == AppRoles.ReadOnlyId).CanAdd);
        var preview = await Roles.PreviewAsync(input);
        Assert.True(preview.Permitted, preview.Rejection);
        Assert.Empty(preview.ReachAdded);
        Assert.Empty(preview.Actor.Added);
        await Roles.SaveEditorAsync(input);
        var baseline = await Roles.GetEditorAsync(AppRoles.ReadOnlyId);
        Assert.False(baseline.Role.CanEdit);
        Assert.All(baseline.Matrix, row => { Assert.False(row.CanAdd); Assert.False(row.CanRemove); });
        var item = await Item(target.Id);
        await Roles.SetParentsAsync(target.Id, [], item.ConcurrencyStamp, await Version());
    }

    [Fact]
    public async Task ReadOnlyParentCannotSupplyPermissionsTheEditorDoesNotHold()
    {
        var target = await Role();
        var actor = await Manager(target.Id);
        await using (var db = database.CreateDbContext())
            await db.UserRoles.Where(x => x.UserId == actor.Id && x.RoleId == AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        var editor = await Roles.GetEditorAsync(target.Id);
        Assert.False(editor.Parents.Single(x => x.Id == AppRoles.ReadOnlyId).CanAdd);
        var input = (await Proposal(target.Id)) with { Parents = [AppRoles.ReadOnlyId] };
        Assert.False((await Roles.PreviewAsync(input)).Permitted);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.SaveEditorAsync(input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.SetParentsAsync(target.Id,
            input.Parents, input.ConcurrencyStamp, input.Version));
        await using var check = database.CreateDbContext();
        Assert.False(await check.RoleInheritance.AnyAsync(x => x.ChildRoleId == target.Id));
    }

    [Theory]
    [InlineData(AppRoles.ReadOnlyId)]
    [InlineData(AppRoles.RootId)]
    public async Task ProtectedRolesStillCannotAcquireParents(string roleId)
    {
        var parent = await Role();
        var input = (await Proposal(roleId)) with { Parents = [parent.Id] };
        Assert.False((await Roles.PreviewAsync(input)).Permitted);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.SaveEditorAsync(input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Roles.SetParentsAsync(roleId,
            input.Parents, input.ConcurrencyStamp, input.Version));
    }
}
