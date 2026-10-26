using System.Data;
using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Authorization;

public sealed record RoleEditorInput(string Id, string Name, string? Description, bool IsEnabled,
    IReadOnlyList<string> Grants, IReadOnlyList<string> Parents, string? ConcurrencyStamp,
    AuthorizationVersion Version, bool Delete = false);
public sealed record RoleRelationship(string Id, string Name, bool Enabled, bool Direct, bool CanAdd, string Reason, bool Inherited = false);
public sealed record RoleEditorView(AuthorizationVersion Version, string? ConcurrencyStamp,
    IReadOnlyList<PermissionMatrixRow> Matrix, IReadOnlyList<RoleRelationship> Parents,
    IReadOnlyList<RoleRelationship> Descendants, int AssignedCount, RoleAdministrationItem Role);
public sealed record RoleImpactItem(string Id, string Name, PermissionImpact Permissions, bool IsEnabled);
public sealed record RoleChangePreview(AuthorizationVersion Version, string? ConcurrencyStamp, bool Permitted,
    string? Rejection, IReadOnlyList<PermissionMatrixRow> Matrix, PermissionImpact SelectedRole,
    IReadOnlyList<RoleImpactItem> Descendants, int ChangedUsers, PermissionImpact Actor,
    IReadOnlyList<string> ReachAdded, IReadOnlyList<string> ReachRemoved, int IncidentRelationships);

public sealed partial class RoleAdministrationService
{
    public async Task<RoleEditorView> GetEditorAsync(string id, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var current = await permissions.EvaluateCurrentInTransactionAsync(db, ct);
        using var op = current.Begin("Roles.Editor", new("Role", id), Permissions.Roles.Read);
        var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("The role no longer exists.");
        var names = await db.Roles.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name ?? "", ct);
        var authority = new AdministrativeAuthority(current);
        var graph = current.Graph;
        var canChange = authority.CanEdit(id) && current.Snapshot.Has(Permissions.Roles.ManageInheritance);
        RoleRelationship Relationship(string other, bool direct) => new(other, names[other], graph.Roles[other].IsEnabled,
            direct, canChange && other != id && authority.CanUseAsParent(other)
                && !graph.Ancestors(other).Contains(id),
            !canChange ? "Locked: Requires Lower-Role Inheritance Authority"
                : other == id || graph.Ancestors(other).Contains(id) ? "Would Create A Cycle"
                : graph.Roles[other].Kind != SystemRoleKind.Baseline && !authority.CanEdit(other)
                    ? "Protected, Directly Held, Or Outside Lower-Role Authority"
                : !current.Snapshot.IsRoot && !graph.StoredEnvelope(other).IsSubsetOf(current.Snapshot.Permissions)
                    ? "Stored Grants Exceed Your Authority" : "Final Graph Safety Checked On Preview And Save", graph.Ancestors(id).Contains(other));
        var parents = graph.Roles.Keys.OrderBy(x => names[x]).Select(x => Relationship(x, graph.DirectParents(id).Contains(x))).ToArray();
        var descendants = graph.AffectedDescendants(id).OrderBy(x => names[x])
            .Select(x => Relationship(x, graph.DirectParents(x).Contains(id))).ToArray();
        var count = await db.UserRoles.CountAsync(x => x.RoleId == id, ct);
        var result = new RoleEditorView(current.Snapshot.Version, role.ConcurrencyStamp,
            AdministrationProjection.Matrix(graph, [id], role.SystemKind == SystemRoleKind.Root, editor: authority, editedRole: id),
            parents, descendants, count, new(role.Id, role.Name ?? "", role.Description, role.IsEnabled, role.SystemKind,
                role.ConcurrencyStamp, graph.DirectGrants(id).Order().ToArray(), graph.DirectParents(id), authority.CanEdit(id),
                role.SystemKind == SystemRoleKind.Ordinary && role.IsEnabled && (current.Snapshot.IsRoot || authority.Anchors.Contains(id)),
                role.SystemKind == SystemRoleKind.Root ? PermissionCatalog.All.Length : graph.EvaluateOrdinary([id]).Count, count));
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<RoleChangePreview> PreviewAsync(RoleEditorInput input, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var current = await permissions.EvaluateCurrentInTransactionAsync(db, ct);
        using var op = current.Begin("Roles.Preview", new("Role", input.Id), Permissions.Roles.Read);
        var role = await GetRole(input.Id, input.ConcurrencyStamp, ct);
        if (current.Snapshot.Version != input.Version)
            throw new DbUpdateConcurrencyException("Authorization changed. Reload and reapply your edits before previewing.");
        var assignedCount = await db.UserRoles.CountAsync(x => x.RoleId == input.Id, ct);
        RoleGraph after;
        string? rejection = null;
        try { after = ValidateEditorChange(current, role, input, assignedCount, op); }
        catch (Exception ex) when (ex is AuthorizationDeniedException or ArgumentException or InvalidOperationException)
        {
            rejection = ex.Message;
            // Invalid proposals still show the current state without inventing a partial result.
            after = current.Graph;
        }
        var before = current.Graph;
        var names = await db.Roles.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name ?? "", ct);
        var affected = before.AffectedDescendants(input.Id).Union(after.Roles.ContainsKey(input.Id)
            ? after.AffectedDescendants(input.Id) : []).OrderBy(x => names[x]).ToArray();
        var descendants = affected.Select(id => new RoleImpactItem(id, names[id],
            AdministrationProjection.Compare(before, after, [id]), after.Roles[id].IsEnabled)).ToArray();
        // Aggregate only: Roles.Read must not disclose account identities or private account data.
        var assignments = await db.UserRoles.Select(x => new { x.UserId, x.RoleId }).ToListAsync(ct);
        var activeUsers = await db.Users.Where(x => x.IsActive).Select(x => x.Id).ToHashSetAsync(ct);
        var rootId = await db.AuthorizationState.Select(x => x.RootUserId).SingleAsync(ct);
        var impactByAssignment = new Dictionary<string, bool>(StringComparer.Ordinal);
        var changedUsers = 0;
        foreach (var user in assignments.Where(x => x.UserId != rootId && activeUsers.Contains(x.UserId)).GroupBy(x => x.UserId))
        {
            var ids = user.Select(x => x.RoleId).Order().ToArray();
            if (!ids.Any(id => id == input.Id || affected.Contains(id))) continue;
            var signature = string.Join("|", ids);
            if (!impactByAssignment.TryGetValue(signature, out var changed))
            {
                var delta = AdministrationProjection.Compare(before, after, ids);
                changed = delta.Added.Count + delta.Removed.Count > 0;
                impactByAssignment[signature] = changed;
            }
            if (changed) changedUsers++;
        }
        var authority = new AdministrativeAuthority(current);
        var matrix = after.Roles.ContainsKey(input.Id)
            ? AdministrationProjection.Matrix(after, [input.Id], role.SystemKind == SystemRoleKind.Root, editor: authority, editedRole: input.Id)
            : AdministrationProjection.Matrix(after, []);
        var oldMatrix = AdministrationProjection.Matrix(before, [input.Id], role.SystemKind == SystemRoleKind.Root);
        var reachBefore = current.DirectRoleIds.Where(id => before.Roles[id].Kind == SystemRoleKind.Ordinary)
            .SelectMany(before.Ancestors).Where(id => before.Roles[id].Kind == SystemRoleKind.Ordinary).ToHashSet();
        var reachAfter = current.DirectRoleIds.Where(id => after.Roles.ContainsKey(id) && after.Roles[id].Kind == SystemRoleKind.Ordinary)
            .SelectMany(after.Ancestors).Where(id => after.Roles[id].Kind == SystemRoleKind.Ordinary).ToHashSet();
        var result = new RoleChangePreview(current.Snapshot.Version, role.ConcurrencyStamp, rejection is null, rejection,
            matrix, AdministrationProjection.Compare(oldMatrix, matrix), descendants, changedUsers,
            AdministrationProjection.Compare(before, after, current.DirectRoleIds, root: current.Snapshot.IsRoot),
            reachAfter.Except(reachBefore).Select(id => names[id]).Order().ToArray(),
            reachBefore.Except(reachAfter).Select(id => names[id]).Order().ToArray(),
            Edges(before).Count(e => e.ChildRoleId == input.Id || e.ParentRoleId == input.Id));
        await tx.CommitAsync(ct);
        return result;
    }

    // Preview and commit share all proposed-state checks. Commit independently repeats them under the security lock.
    private static RoleGraph ValidateEditorChange(AuthorizationEvaluation current, ApplicationRole role,
        RoleEditorInput input, int assignments, AuthorizedOperation op)
    {
        var graph = current.Graph;
        var authority = new AdministrativeAuthority(current);
        AdministrativeAuthority.Require(authority.CanEdit(input.Id), "Only strictly lower ordinary roles may be edited.");
        if (input.Delete)
        {
            op.Require(op.Purpose, op.Scope, Permissions.Roles.Delete);
            if (assignments != 0) throw new InvalidOperationException("Remove all user assignments before deleting this role.");
            var deleted = new RoleGraph(graph.Roles.Values.Where(x => x.Id != input.Id),
                Grants(graph).Where(x => x.RoleId != input.Id),
                Edges(graph).Where(x => x.ChildRoleId != input.Id && x.ParentRoleId != input.Id));
            authority.ValidateResult(deleted, input.Id);
            return deleted;
        }
        var name = ValidateName(input.Name);
        var description = ValidateDescription(input.Description);
        if (name != role.Name || description != role.Description || input.IsEnabled != role.IsEnabled)
            op.Require(op.Purpose, op.Scope, Permissions.Roles.Update);
        if (input.IsEnabled != role.IsEnabled || !graph.DirectGrants(input.Id).SetEquals(input.Grants))
            op.Require(op.Purpose, op.Scope, Permissions.Roles.ManagePermissions);
        if (!graph.DirectParents(input.Id).ToHashSet().SetEquals(input.Parents))
            op.Require(op.Purpose, op.Scope, Permissions.Roles.ManageInheritance);
        var parentsChanged = !graph.DirectParents(input.Id).ToHashSet().SetEquals(input.Parents);
        var after = BuildSecurityGraph(current, input.Id, input.Grants, parentsChanged ? input.Parents : null, input.IsEnabled);
        authority.ValidateResult(after, input.Id);
        return after;
    }

    private static RoleGraph BuildSecurityGraph(AuthorizationEvaluation current, string id,
        IReadOnlyList<string>? grants, IReadOnlyList<string>? parents, bool? enabled = null)
    {
        var authority = new AdministrativeAuthority(current);
        if (grants is not null)
        {
            if (grants.Distinct().Count() != grants.Count) throw new ArgumentException("Duplicate permission.");
            foreach (var added in grants.Except(current.Graph.DirectGrants(id)))
                AdministrativeAuthority.Require(PermissionCatalog.ByKey.TryGetValue(added, out var definition)
                    && definition.Authority == PermissionAuthority.Ordinary && current.Snapshot.Has(added),
                    "Added permissions must be recognized ordinary permissions you currently possess.");
        }
        if (parents is not null)
        {
            if (parents.Distinct().Count() != parents.Count) throw new ArgumentException("Duplicate parent role.");
            foreach (var parent in parents.Except(current.Graph.DirectParents(id)))
                AdministrativeAuthority.Require(parent != id && authority.CanUseAsParent(parent),
                    "An added parent must be ReadOnly or an eligible strictly lower ordinary role within your authority.");
            AdministrativeAuthority.Require(parents.All(p => current.Graph.Roles.TryGetValue(p, out var r)
                && r.Kind != SystemRoleKind.Root), "Root cannot participate in role inheritance.");
        }
        try
        {
            return new(current.Graph.Roles.Values.Select(x => x.Id == id && enabled is bool value ? x with { IsEnabled = value } : x),
                grants is null ? Grants(current.Graph) : Grants(current.Graph).Where(x => x.RoleId != id).Concat(grants.Select(k => new GraphGrant(id, k))),
                parents is null ? Edges(current.Graph) : Edges(current.Graph).Where(x => x.ChildRoleId != id).Concat(parents.Select(p => new GraphEdge(id, p))));
        }
        catch (InvalidOperationException ex) { throw new ArgumentException(ex.Message); }
    }

    public async Task SaveEditorAsync(RoleEditorInput input, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await LockAndEvaluate(input.Version, ct);
        using var op = current.Begin("Roles.Edit", new("Role", input.Id), Permissions.Roles.Read);
        var role = await GetRole(input.Id, input.ConcurrencyStamp, ct);
        var assignments = await db.UserRoles.CountAsync(x => x.RoleId == input.Id, ct);
        ValidateEditorChange(current, role, input, assignments, op);
        var details = new List<AuthorizationChangeDetail>();
        var oldGrants = await db.RolePermissions.Where(x => x.RoleId == input.Id).ToListAsync(ct);
        var oldEdges = await db.RoleInheritance.Where(x => x.ChildRoleId == input.Id || input.Delete && x.ParentRoleId == input.Id).ToListAsync(ct);
        foreach (var grant in oldGrants.Where(x => input.Delete || !input.Grants.Contains(x.PermissionKey)))
        {
            db.Remove(grant);
            details.Add(new() { Kind = AuthorizationChangeKind.Grant, RoleId = input.Id, PermissionKey = grant.PermissionKey, WasPresent = true, IsPresent = false });
        }
        foreach (var edge in oldEdges.Where(x => input.Delete || !input.Parents.Contains(x.ParentRoleId)))
        {
            db.Remove(edge);
            details.Add(new() { Kind = AuthorizationChangeKind.Inheritance, RoleId = edge.ChildRoleId, ParentRoleId = edge.ParentRoleId, WasPresent = true, IsPresent = false });
        }
        await db.SaveChangesAsync(ct);
        if (input.Delete)
        {
            details.Add(new() { Kind = AuthorizationChangeKind.Role, RoleId = input.Id, WasPresent = true, IsPresent = false, PreviousName = role.Name });
            Check(await roles.DeleteAsync(role));
        }
        else
        {
            foreach (var key in input.Grants.Except(oldGrants.Select(x => x.PermissionKey)))
            {
                db.RolePermissions.Add(new() { RoleId = input.Id, PermissionKey = key });
                details.Add(new() { Kind = AuthorizationChangeKind.Grant, RoleId = input.Id, PermissionKey = key, WasPresent = false, IsPresent = true });
            }
            foreach (var parent in input.Parents.Except(oldEdges.Select(x => x.ParentRoleId)))
            {
                db.RoleInheritance.Add(new() { ChildRoleId = input.Id, ParentRoleId = parent });
                details.Add(new() { Kind = AuthorizationChangeKind.Inheritance, RoleId = input.Id, ParentRoleId = parent, WasPresent = false, IsPresent = true });
            }
            var name = ValidateName(input.Name); var description = ValidateDescription(input.Description);
            if (name != role.Name || description != role.Description || input.IsEnabled != role.IsEnabled)
                details.Add(new() { Kind = AuthorizationChangeKind.Role, RoleId = input.Id, WasPresent = true, IsPresent = true,
                    PreviousName = role.Name, ResultingName = name, WasEnabled = role.IsEnabled, IsEnabled = input.IsEnabled,
                    FieldName = "Description", PreviousValue = role.Description, ResultingValue = description });
            if (details.Count == 0) { await tx.CommitAsync(ct); return; }
            role.Name = name; role.Description = description; role.IsEnabled = input.IsEnabled;
            Check(await roles.UpdateAsync(role));
            await db.SaveChangesAsync(ct);
        }
        await AuthorizationAudit.RecordAsync(db, op, input.Version.Epoch, input.Delete ? "Delete ordinary role without reparenting" : "Edit ordinary role", details, clock, ct);
        await tx.CommitAsync(ct);
        notifications?.Publish();
    }
}
