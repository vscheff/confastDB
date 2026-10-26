using Confast.Web.Components;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    private async Task<RoleEditorInput> Proposal(string id)
    {
        var item = await Item(id); var editor = await Roles.GetEditorAsync(id);
        return new(id, item.Name, item.Description, item.IsEnabled, item.Grants, item.Parents, editor.ConcurrencyStamp, editor.Version);
    }

    [Fact]
    public async Task MatrixCatalogProvenanceDiamondsAndDisabledPathsAreAccurate()
    {
        var source = await Role([Permissions.Chat.SendMessages]);
        var left = await Role([], true, source.Id); var right = await Role([Permissions.Chat.SendMessages], false, source.Id);
        var target = await Role([Permissions.Parts.Create], true, left.Id, right.Id);
        var view = await Roles.GetEditorAsync(target.Id);
        Assert.Equal(164, view.Matrix.Count);
        var send = view.Matrix.Single(x => x.Definition.Key == Permissions.Chat.SendMessages);
        Assert.False(send.Direct); Assert.True(send.Inherited); Assert.True(send.Effective); Assert.False(send.Usable);
        Assert.Equal([Permissions.Chat.Access], send.MissingPrerequisites);
        var shared = send.Sources.Single(x => x.RoleId == source.Id);
        Assert.True(shared.IsActive); Assert.Equal(2, shared.Paths.Count);
        Assert.Contains(shared.Paths, x => x.IsEnabled); Assert.Contains(shared.Paths, x => !x.IsEnabled);
        Assert.False(send.Sources.Single(x => x.RoleId == right.Id).IsActive);
        Assert.False(send.Sources.Single(x => x.RoleId == right.Id).GrantRoleEnabled);
        Assert.True(shared.GrantRoleEnabled);
        Assert.True(view.Matrix.Single(x => x.Definition.Key == Permissions.Parts.Create).Direct);
        Assert.Contains(view.Parents, x => x.Id == source.Id && x.Inherited && !x.Direct);
    }

    [Fact]
    public async Task ProvenanceImpactIncludesChangesBeyondTheDisplayedPathLimit()
    {
        var source = await Role([Permissions.Parts.Read]);
        var branches = new List<string>();
        for (var index = 0; index < 14; index++) branches.Add((await Role([], true, source.Id)).Id);
        var target = await Role([], true, branches.ToArray());
        var before = (await Roles.GetEditorAsync(target.Id)).Matrix.Single(x => x.Definition.Key == Permissions.Parts.Read);
        Assert.Equal(12, before.Sources.Single().Paths.Count); Assert.True(before.Sources.Single().MorePaths);
        var input = (await Proposal(target.Id)) with { Parents = branches.Order(StringComparer.Ordinal).SkipLast(1).ToArray() };
        var preview = await Roles.PreviewAsync(input);
        var after = preview.Matrix.Single(x => x.Definition.Key == Permissions.Parts.Read);
        Assert.True(preview.Permitted); Assert.True(after.Sources.Single().MorePaths);
        Assert.Equal(before.Sources.Single().Paths.Select(x => string.Join("/", x.Roles)), after.Sources.Single().Paths.Select(x => string.Join("/", x.Roles)));
        Assert.Contains(Permissions.Parts.Read, preview.SelectedRole.ProvenanceChanged);
        Assert.Empty(preview.SelectedRole.Removed);
    }

    [Fact]
    public async Task FailedAdvisorySubscriberCannotFailAnAlreadyCommittedSecurityEdit()
    {
        var target = await Role(); var input = (await Proposal(target.Id)) with { Name = "Committed Despite Subscriber Failure" };
        var signals = services.GetRequiredService<AuthorizationChangeNotifications>();
        var delivered = 0;
        signals.Changed += () => throw new InvalidOperationException("Simulated disconnected renderer");
        signals.Changed += () => delivered++;
        await Roles.SaveEditorAsync(input);
        Assert.Equal(input.Name, (await Item(target.Id)).Name); Assert.Equal(1, delivered);
        await using var db = database.CreateDbContext();
        Assert.Single(await db.AuthorizationChangeHistory.Where(x => x.Purpose == "Roles.Edit").ToArrayAsync());
    }

    [Fact]
    public async Task ProtectedRootAndOptionalReadOnlyHaveHonestMatrixStates()
    {
        var root = await Roles.GetEditorAsync(AppRoles.RootId);
        Assert.All(root.Matrix, row => { Assert.True(row.Effective); Assert.True(row.Usable); Assert.False(row.Direct); Assert.False(row.CanAdd); Assert.False(row.CanRemove); });
        var baseline = await Roles.GetEditorAsync(AppRoles.ReadOnlyId);
        Assert.All(baseline.Matrix, row => { Assert.False(row.CanAdd); Assert.False(row.CanRemove); });
        var target = await User();
        var edit = (await Users.GetUserForEditAsync(target.Id))!; edit.Roles.Clear();
        Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
        var access = await Users.GetEffectiveAccessAsync(target.Id);
        Assert.Empty(access.Roles); Assert.DoesNotContain(access.Matrix, row => row.Effective || row.Direct);
        var owner = await Users.GetEffectiveAccessAsync(rootId);
        Assert.True(owner.IsRoot); Assert.Null(owner.ConcurrencyStamp);
    }

    [Fact]
    public async Task RemoveOnlyAndInheritedSurvivalAreRepresentedAndCommitted()
    {
        var lower = await Role([Permissions.Chat.SendMessages]);
        await Manager(lower.Id);
        var editor = await Roles.GetEditorAsync(lower.Id);
        var row = editor.Matrix.Single(x => x.Definition.Key == Permissions.Chat.SendMessages);
        // The actor inherits this source, so revoke only the actor's operational branch while preserving management reach.
        // A disabled target stores the key but does not supply it to the enabled management anchor.
        await using (var db = database.CreateDbContext())
            await db.Roles.Where(x => x.Id == lower.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsEnabled, false));
        editor = await Roles.GetEditorAsync(lower.Id);
        row = editor.Matrix.Single(x => x.Definition.Key == Permissions.Chat.SendMessages);
        Assert.True(row.CanRemove); Assert.False(row.CanAdd); Assert.False(row.Effective);
        var input = (await Proposal(lower.Id)) with { Grants = [] };
        Assert.True((await Roles.PreviewAsync(input)).Permitted);
        await Roles.SaveEditorAsync(input);
        Assert.DoesNotContain((await Item(lower.Id)).Grants, x => x == Permissions.Chat.SendMessages);
    }

    [Fact]
    public async Task RemovingDirectGrantPreservesInheritedEffectAndChangesProvenanceOnly()
    {
        var parent = await Role([Permissions.Parts.Read]);
        var target = await Role([Permissions.Parts.Read], true, parent.Id);
        var input = (await Proposal(target.Id)) with { Grants = [] };
        var preview = await Roles.PreviewAsync(input);
        Assert.True(preview.Permitted); Assert.Empty(preview.SelectedRole.Removed);
        Assert.Contains(Permissions.Parts.Read, preview.SelectedRole.ProvenanceChanged);
        await Roles.SaveEditorAsync(input);
        var row = (await Roles.GetEditorAsync(target.Id)).Matrix.Single(x => x.Definition.Key == Permissions.Parts.Read);
        Assert.False(row.Direct); Assert.True(row.Inherited); Assert.True(row.Effective);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("parents")]
    [InlineData("disable")]
    [InlineData("delete")]
    public async Task PreviewShowsDescendantsUsersAndDormantEnvelopesWithoutWriting(string kind)
    {
        var parent = await Role([Permissions.Chat.Access]);
        var target = await Role([Permissions.Chat.SendMessages], true, parent.Id);
        var child = await Role([], true, target.Id); var dormant = await Role([], false, target.Id);
        var targetUser = await User(child.Id); await User(child.Id, parent.Id);
        var input = await Proposal(target.Id);
        input = kind switch
        {
            "grant" => input with { Grants = [] }, "parents" => input with { Parents = [] },
            "disable" => input with { IsEnabled = false }, _ => input with { Delete = true }
        };
        await using var beforeDb = database.CreateDbContext();
        var audit = await beforeDb.AuthorizationChangeHistory.CountAsync();
        var epoch = await Version();
        var notifications = services.GetRequiredService<AuthorizationChangeNotifications>();
        var hints = 0; notifications.Changed += () => hints++;
        var preview = await Roles.PreviewAsync(input);
        Assert.True(preview.Permitted, preview.Rejection); Assert.Equal(epoch, preview.Version);
        Assert.Equal(0, hints); Assert.Equal(epoch, await Version());
        Assert.Equal(audit, await beforeDb.AuthorizationChangeHistory.CountAsync());
        Assert.Contains(preview.Descendants, x => x.Id == child.Id && x.Permissions.HasChanges);
        var dormantChange = preview.Descendants.Single(x => x.Id == dormant.Id).Permissions;
        Assert.Empty(dormantChange.Added); Assert.Empty(dormantChange.Removed);
        if (kind != "disable") Assert.NotEmpty(dormantChange.StoredRemoved);
        Assert.True(preview.ChangedUsers > 0);
        if (kind == "parents") Assert.Contains(Permissions.Chat.SendMessages, preview.SelectedRole.NewlyUnavailable);
        var json = System.Text.Json.JsonSerializer.Serialize(preview);
        Assert.DoesNotContain(targetUser.Id, json); Assert.DoesNotContain(rootId, json);
        Assert.DoesNotContain("SecurityStamp", json);
        await Roles.SaveEditorAsync(input); Assert.Equal(1, hints);
    }

    [Theory]
    [InlineData("rootkey")]
    [InlineData("unknown")]
    [InlineData("cycle")]
    [InlineData("reach")]
    [InlineData("assigned-delete")]
    [InlineData("protected")]
    public async Task PreviewAndCommitRejectTheSameUnsafeChanges(string kind)
    {
        var target = await Role(); var child = await Role([], true, target.Id);
        if (kind == "assigned-delete") await User(target.Id);
        if (kind == "reach") await Manager(target.Id);
        var input = await Proposal(kind == "protected" ? AppRoles.ReadOnlyId : target.Id);
        input = kind switch
        {
            "rootkey" => input with { Grants = [Permissions.Authorization.ManageSecurity] },
            "unknown" => input with { Grants = ["MadeUp.Permission"] },
            "cycle" => input with { Parents = [child.Id] },
            "reach" => input with { Parents = [(await Role()).Id], Version = await Version() },
            "assigned-delete" => input with { Delete = true }, _ => input with { Name = "Protected Change" }
        };
        var preview = await Roles.PreviewAsync(input);
        Assert.False(preview.Permitted); Assert.NotNull(preview.Rejection);
        var exception = await Record.ExceptionAsync(() => Roles.SaveEditorAsync(input)); Assert.NotNull(exception);
        Assert.True(exception is AuthorizationDeniedException or ArgumentException or InvalidOperationException);
    }

    [Fact]
    public async Task CombinedSaveAuditsAtomicallyAndRejectsStalePreviewEpochAndRoleStamp()
    {
        var parent = await Role([Permissions.Parts.Read]); var target = await Role();
        var input = (await Proposal(target.Id)) with { Name = "Combined Role", Grants = [Permissions.Chat.SendMessages], Parents = [parent.Id], IsEnabled = false };
        Assert.True((await Roles.PreviewAsync(input)).Permitted);
        await Roles.SaveEditorAsync(input);
        var item = await Item(target.Id); Assert.Equal("Combined Role", item.Name); Assert.False(item.IsEnabled);
        Assert.Equal([parent.Id], item.Parents); Assert.Equal([Permissions.Chat.SendMessages], item.Grants);
        await using var db = database.CreateDbContext();
        var history = await db.AuthorizationChangeHistory.Include(x => x.Details).SingleAsync(x => x.Purpose == "Roles.Edit");
        Assert.Contains(history.Details, x => x.Kind == AuthorizationChangeKind.Grant);
        Assert.Contains(history.Details, x => x.Kind == AuthorizationChangeKind.Inheritance);
        Assert.Contains(history.Details, x => x.Kind == AuthorizationChangeKind.Role);
        Assert.True(history.ResultingEpoch > history.PreviousEpoch);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Roles.SaveEditorAsync(input));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Roles.PreviewAsync(input));
        var fresh = (await Proposal(target.Id)) with { ConcurrencyStamp = "stale" };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Roles.SaveEditorAsync(fresh));
    }

    [Fact]
    public async Task AssignmentPreviewHandlesMultipleRolesAndDoesNotInvalidateTheExistingSession()
    {
        var shared = await Role([Permissions.Chat.Access, Permissions.Chat.SendMessages]);
        var other = await Role([Permissions.Chat.Access]); var user = await User(shared.Id, other.Id);
        var edit = (await Users.GetUserForEditAsync(user.Id))!;
        edit.Roles.Remove(shared.Name!);
        var preview = await Users.GetEffectiveAccessAsync(user.Id, edit.Roles, edit.Version, edit.ConcurrencyStamp);
        Assert.True(preview.Permitted); Assert.Contains(Permissions.Chat.SendMessages, preview.Impact!.Removed);
        Assert.DoesNotContain(Permissions.Chat.Access, preview.Impact.Removed);
        Assert.Contains(Permissions.Chat.Access, preview.Impact.ProvenanceChanged);
        await session.SignInAsync(user.Id); var savedPrincipal = session.Principal;
        Assert.True((await Evaluator.EvaluateCurrentAsync()).Has(Permissions.Chat.SendMessages));
        await session.SignInAsync(rootId);
        Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
        session.Principal = savedPrincipal;
        var refreshed = await Evaluator.EvaluateCurrentAsync();
        Assert.False(refreshed.Has(Permissions.Chat.SendMessages)); Assert.True(refreshed.Has(Permissions.Chat.Access));
        await using var db = database.CreateDbContext();
        Assert.Equal(user.SecurityStamp, await db.Users.Where(x => x.Id == user.Id).Select(x => x.SecurityStamp).SingleAsync());
        await session.SignInAsync(rootId);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Users.GetEffectiveAccessAsync(user.Id, edit.Roles, edit.Version, edit.ConcurrencyStamp));
    }

    [Fact]
    public async Task ReadOnlyViewerCanInspectButCannotPreviewMutationsOrWrite()
    {
        var target = await Role(); var readRole = await Role([Permissions.Roles.Read, Permissions.Users.Read]);
        var user = await User(); var actor = await User(readRole.Id); await session.SignInAsync(actor.Id);
        var editor = await Roles.GetEditorAsync(target.Id);
        Assert.Equal(164, editor.Matrix.Count); Assert.All(editor.Matrix, x => { Assert.False(x.CanAdd); Assert.False(x.CanRemove); });
        var preview = await Roles.PreviewAsync((await Proposal(target.Id)) with { Name = "Denied" });
        Assert.False(preview.Permitted);
        Assert.NotEmpty((await Users.GetEffectiveAccessAsync(user.Id)).Matrix);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Users.GetEffectiveAccessAsync(user.Id, []));
    }

    [Fact]
    public async Task AdvisoryRefreshRetainsUnsavedModelWithoutReloadingOrPolling()
    {
        var lower = await Role([Permissions.Customers.Read, Permissions.Customers.Update]);
        var actor = await User(lower.Id); await session.SignInAsync(actor.Id);
        PermissionProbeHost host = null!; PermissionRefreshProbe probe = null!;
        Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent rendered = default;
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var evaluator = services.GetRequiredService<EffectivePermissionService>();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            rendered = await renderer.RenderComponentAsync<PermissionProbeHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PermissionProbeHost.Capture)] = (Action<PermissionProbeHost>)(value => host = value),
                [nameof(PermissionProbeHost.CaptureProbe)] = (Action<PermissionRefreshProbe>)(value => probe = value)
            }));
        });
        Assert.True(probe.Allowed); Assert.Equal(1, probe.Loads);
        probe.Draft = "Recoverable Unsaved Input";
        await using (var db = database.CreateDbContext())
            await db.RolePermissions.Where(x => x.RoleId == lower.Id && x.PermissionKey == Permissions.Customers.Update).ExecuteDeleteAsync();
        var snapshots = evaluator.SnapshotCount;
        await renderer.Dispatcher.InvokeAsync(() => host.Invalidate());
        await probe.Refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await renderer.Dispatcher.InvokeAsync(() => rendered.QuiescenceTask);
        Assert.False(probe.Allowed); Assert.Equal("Recoverable Unsaved Input", probe.Draft); Assert.Equal(1, probe.Loads);
        Assert.InRange(evaluator.SnapshotCount - snapshots, 1, 2);
        // A cascading delivery and the enclosing parent render can each refresh once; neither is a poll.
        await Task.Delay(50);
        Assert.InRange(evaluator.SnapshotCount - snapshots, 1, 2);
        Assert.Equal("Recoverable Unsaved Input", probe.Draft); Assert.Equal(1, probe.Loads);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => session.Guard.BeginAsync("Write", new("Customers"), [Permissions.Customers.Update]));
    }
}

public sealed class PermissionRefreshProbe : PermissionComponentBase
{
    [Parameter] public Action<PermissionRefreshProbe> Capture { get; set; } = null!;
    public int Loads { get; private set; }
    public string Draft { get; set; } = "";
    public bool Allowed => Can(Permissions.Customers.Update);
    public TaskCompletionSource Refreshed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override void OnInitialized() => Capture(this);
    protected override void OnParametersSet() { Loads++; Draft = "Loaded"; }
    protected override Task OnPermissionsChangedAsync() { Refreshed.TrySetResult(); return Task.CompletedTask; }
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, Allowed);
}
public sealed class PermissionProbeHost : ComponentBase
{
    [Parameter] public Action<PermissionProbeHost> Capture { get; set; } = null!;
    [Parameter] public Action<PermissionRefreshProbe> CaptureProbe { get; set; } = null!;
    private long revision;
    protected override void OnInitialized() => Capture(this);
    public void Invalidate() { revision++; StateHasChanged(); }
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<long>>(0);
        builder.AddAttribute(1, "Name", "AuthorizationRevision"); builder.AddAttribute(2, "Value", revision);
        builder.AddAttribute(3, "ChildContent", (RenderFragment)(child =>
        { child.OpenComponent<PermissionRefreshProbe>(0); child.AddAttribute(1, "Capture", CaptureProbe); child.CloseComponent(); }));
        builder.CloseComponent();
    }
}

public sealed class AdministrationUiPresentationTests
{
    [Fact]
    public void ConflictRecoveryPreservesIntentAndUnrelatedCommittedChanges()
    {
        var baseline = new RoleEditorInput("role", "Original", null, true, ["A", "B"], ["P"], "old", default);
        var draft = new RoleEditDraft(baseline); draft.Grants.Remove("A"); draft.Grants.Add("C"); draft.Description = "Local";
        var latest = baseline with { Name = "Remote", Grants = ["A", "B", "D"], ConcurrencyStamp = "new" };
        draft.Reapply(latest);
        Assert.Equal("Remote", draft.Name); Assert.Equal("Local", draft.Description); Assert.Equal("new", draft.Input().ConcurrencyStamp);
        Assert.True(draft.Grants.SetEquals(["B", "C", "D"])); Assert.True(draft.IsDirty);
        var oldUser = new EditUserInput { Id = "user", DisplayName = "Original", Roles = ["A", "B"] };
        var local = UserEditRecovery.Copy(oldUser); local.Roles.Remove("A"); local.JobTitle = "Local";
        var remote = UserEditRecovery.Copy(oldUser); remote.Roles.Add("C"); remote.DisplayName = "Remote";
        var merged = UserEditRecovery.Reapply(oldUser, local, remote);
        Assert.True(merged.Roles.SetEquals(["B", "C"])); Assert.Equal("Remote", merged.DisplayName); Assert.Equal("Local", merged.JobTitle);
    }

    [Fact]
    public async Task CatalogBackedMatrixRendersEveryKeyAndPrerequisitesWithoutChangingSelection()
    {
        var rows = PermissionCatalog.All.Select(p => new PermissionMatrixRow(p, false, false, false, false, p.Prerequisites,
            [], false, false, "Locked")).ToArray();
        var selected = new HashSet<string> { Permissions.Chat.SendMessages };
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<PermissionMatrix>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PermissionMatrix.Rows)] = rows,
                [nameof(PermissionMatrix.SelectedGrants)] = selected }))).ToHtmlString());
        foreach (var key in PermissionCatalog.ByKey.Keys) Assert.Contains(key, html);
        Assert.Contains("Required Prerequisites", html); Assert.Contains("Functional Category", html);
        Assert.Equal([Permissions.Chat.SendMessages], selected);
    }
}
