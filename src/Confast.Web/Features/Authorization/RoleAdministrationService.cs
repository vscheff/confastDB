using System.Data;
using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Authorization;

public sealed record RoleAdministrationItem(string Id, string Name, string? Description, bool IsEnabled,
    SystemRoleKind Kind, string? ConcurrencyStamp, IReadOnlyList<string> Grants, IReadOnlyList<string> Parents,
    bool CanEdit, bool CanAnchor);
public sealed record RoleAdministrationView(AuthorizationVersion Version, IReadOnlyList<RoleAdministrationItem> Roles,
    IReadOnlySet<string> ActorPermissions);
public sealed record CreateRoleInput(string Name, string? Description, string? AnchorRoleId, AuthorizationVersion Version);
public sealed record EditRoleInput(string Id, string Name, string? Description, bool IsEnabled,
    string? ConcurrencyStamp, AuthorizationVersion Version);

public sealed class RoleAdministrationService(AppDbContext db, RoleManager<ApplicationRole> roles,
    EffectivePermissionService permissions, TimeProvider clock)
{
    public async Task<RoleAdministrationView> GetRolesAsync(CancellationToken ct = default)
    {
        // Identity entities tracked by an earlier circuit event must never supply authority.
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var current = await permissions.EvaluateCurrentInTransactionAsync(db, ct);
        using var operation = current.Begin("Roles.Read", new("Roles"), Permissions.Roles.Read);
        var authority = new AdministrativeAuthority(current);
        var values = await db.Roles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var result = values.Select(x => new RoleAdministrationItem(x.Id, x.Name ?? "", x.Description,
            x.IsEnabled, x.SystemKind, x.ConcurrencyStamp, current.Graph.DirectGrants(x.Id).Order().ToArray(),
            current.Graph.DirectParents(x.Id), authority.CanEdit(x.Id), x.SystemKind == SystemRoleKind.Ordinary
                && x.IsEnabled && (current.Snapshot.IsRoot || authority.Anchors.Contains(x.Id)))).ToArray();
        await tx.CommitAsync(ct);
        return new(current.Snapshot.Version, result, current.Snapshot.Permissions);
    }

    public async Task<string> CreateAsync(CreateRoleInput input, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await AuthorizationMutationLock.AcquireAsync(db, input.Version, ct);
        var current = await permissions.EvaluateCurrentInTransactionAsync(db, ct);
        using var op = current.Begin("Roles.Create", new("Roles"), Permissions.Roles.Create);
        var authority = new AdministrativeAuthority(current);
        if (input.AnchorRoleId is { } anchor)
            AdministrativeAuthority.Require(current.Graph.Roles.TryGetValue(anchor, out var a)
                && a.Kind == SystemRoleKind.Ordinary && a.IsEnabled
                && (current.Snapshot.IsRoot || authority.Anchors.Contains(anchor)), "Select an enabled directly assigned ordinary anchor.");
        else AdministrativeAuthority.Require(current.Snapshot.IsRoot, "Ordinary role creation requires a direct administrative anchor.");
        var role = new ApplicationRole { Name = ValidateName(input.Name), Description = ValidateDescription(input.Description) };
        var proposedRoles = current.Graph.Roles.Values.Append(new GraphRole(role.Id, SystemRoleKind.Ordinary, true));
        var proposedEdges = Edges(current.Graph).ToList();
        if (input.AnchorRoleId is not null) proposedEdges.Add(new(input.AnchorRoleId, role.Id));
        var after = new RoleGraph(proposedRoles, Grants(current.Graph), proposedEdges);
        // This endpoint only constructs an empty fresh role and this one prescribed edge.
        authority.ValidateResult(after, role.Id);
        Check(await roles.CreateAsync(role));
        var details = new List<AuthorizationChangeDetail> { new() { Kind = AuthorizationChangeKind.Role, RoleId = role.Id,
            WasPresent = false, IsPresent = true, ResultingName = role.Name, IsEnabled = true,
            FieldName = "Description", ResultingValue = role.Description } };
        if (input.AnchorRoleId is not null)
        {
            db.RoleInheritance.Add(new() { ChildRoleId = input.AnchorRoleId, ParentRoleId = role.Id });
            details.Add(new() { Kind = AuthorizationChangeKind.Inheritance, RoleId = input.AnchorRoleId,
                ParentRoleId = role.Id, WasPresent = false, IsPresent = true });
            await db.SaveChangesAsync(ct);
        }
        await AuthorizationAudit.RecordAsync(db, op, input.Version.Epoch, "Create empty ordinary role", details, clock, ct, input.AnchorRoleId);
        await tx.CommitAsync(ct);
        return role.Id;
    }

    public async Task UpdateAsync(EditRoleInput input, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await LockAndEvaluate(input.Version, ct);
        using var op = current.Begin("Roles.Update", new("Role", input.Id), Permissions.Roles.Update);
        var authority = new AdministrativeAuthority(current);
        AdministrativeAuthority.Require(authority.CanEdit(input.Id), "Only strictly lower ordinary roles may be edited.");
        var role = await GetRole(input.Id, input.ConcurrencyStamp, ct);
        if (input.IsEnabled != role.IsEnabled) op.Require(op.Purpose, op.Scope, Permissions.Roles.ManagePermissions);
        var after = new RoleGraph(current.Graph.Roles.Values.Select(x => x.Id == input.Id ? x with { IsEnabled = input.IsEnabled } : x),
            Grants(current.Graph), Edges(current.Graph));
        authority.ValidateResult(after, input.Id);
        var detail = new AuthorizationChangeDetail { Kind = AuthorizationChangeKind.Role, RoleId = role.Id,
            WasPresent = true, IsPresent = true, PreviousName = role.Name, ResultingName = ValidateName(input.Name),
            WasEnabled = role.IsEnabled, IsEnabled = input.IsEnabled,
            FieldName = "Description", PreviousValue = role.Description, ResultingValue = ValidateDescription(input.Description) };
        role.Name = detail.ResultingName; role.Description = detail.ResultingValue; role.IsEnabled = input.IsEnabled;
        Check(await roles.UpdateAsync(role));
        await AuthorizationAudit.RecordAsync(db, op, input.Version.Epoch, "Update ordinary role", [detail], clock, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetGrantsAsync(string id, IEnumerable<string> keys, string? stamp, AuthorizationVersion version, CancellationToken ct = default)
        => await SetSecurityAsync(id, keys, null, stamp, version, ct);

    public async Task SetParentsAsync(string id, IEnumerable<string> parentIds, string? stamp, AuthorizationVersion version, CancellationToken ct = default)
        => await SetSecurityAsync(id, null, parentIds, stamp, version, ct);

    private async Task SetSecurityAsync(string id, IEnumerable<string>? grants, IEnumerable<string>? parents,
        string? stamp, AuthorizationVersion version, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await LockAndEvaluate(version, ct);
        var key = grants is not null ? Permissions.Roles.ManagePermissions : Permissions.Roles.ManageInheritance;
        using var op = current.Begin(key, new("Role", id), key);
        var authority = new AdministrativeAuthority(current);
        AdministrativeAuthority.Require(authority.CanEdit(id), "Only strictly lower ordinary roles may be edited.");
        var role = await GetRole(id, stamp, ct);
        var newGrants = grants?.ToArray(); var newParents = parents?.ToArray();
        if (newGrants is not null)
        {
            if (newGrants.Distinct().Count() != newGrants.Length) throw new ArgumentException("Duplicate permission.");
            foreach (var added in newGrants.Except(current.Graph.DirectGrants(id)))
                AdministrativeAuthority.Require(PermissionCatalog.ByKey.TryGetValue(added, out var definition)
                    && definition.Authority == PermissionAuthority.Ordinary && current.Snapshot.Has(added),
                    "Added permissions must be recognized ordinary permissions you currently possess.");
        }
        if (newParents is not null)
        {
            if (newParents.Distinct().Count() != newParents.Length) throw new ArgumentException("Duplicate parent role.");
            foreach (var parent in newParents.Except(current.Graph.DirectParents(id)))
                AdministrativeAuthority.Require(parent != id && authority.CanEdit(parent)
                    && (current.Snapshot.IsRoot || current.Graph.StoredEnvelope(parent).IsSubsetOf(current.Snapshot.Permissions)),
                    "An added parent must be an eligible strictly lower ordinary role within your authority.");
            AdministrativeAuthority.Require(newParents.All(p => current.Graph.Roles.TryGetValue(p, out var r)
                && r.Kind == SystemRoleKind.Ordinary), "Protected baseline and Root relationships cannot be edited.");
        }
        RoleGraph after;
        try { after = new(current.Graph.Roles.Values,
            newGrants is null ? Grants(current.Graph) : Grants(current.Graph).Where(x => x.RoleId != id).Concat(newGrants.Select(k => new GraphGrant(id, k))),
            newParents is null ? Edges(current.Graph) : Edges(current.Graph).Where(x => x.ChildRoleId != id).Concat(newParents.Select(p => new GraphEdge(id, p)))); }
        catch (InvalidOperationException exception) { throw new ArgumentException(exception.Message); }
        authority.ValidateResult(after, id);
        var details = new List<AuthorizationChangeDetail>();
        if (newGrants is not null)
        {
            var existing = await db.RolePermissions.Where(x => x.RoleId == id).ToListAsync(ct);
            foreach (var row in existing.Where(x => !newGrants.Contains(x.PermissionKey)))
            { db.Remove(row); details.Add(new() { Kind = AuthorizationChangeKind.Grant, RoleId = id, PermissionKey = row.PermissionKey, WasPresent = true, IsPresent = false }); }
            foreach (var added in newGrants.Except(existing.Select(x => x.PermissionKey)))
            { db.RolePermissions.Add(new() { RoleId = id, PermissionKey = added }); details.Add(new() { Kind = AuthorizationChangeKind.Grant, RoleId = id, PermissionKey = added, WasPresent = false, IsPresent = true }); }
        }
        if (newParents is not null)
        {
            var existing = await db.RoleInheritance.Where(x => x.ChildRoleId == id).ToListAsync(ct);
            foreach (var row in existing.Where(x => !newParents.Contains(x.ParentRoleId)))
            { db.Remove(row); details.Add(new() { Kind = AuthorizationChangeKind.Inheritance, RoleId = id, ParentRoleId = row.ParentRoleId, WasPresent = true, IsPresent = false }); }
            // Remove old edges before inserts; even the transient graph must remain acyclic.
            await db.SaveChangesAsync(ct);
            foreach (var added in newParents.Except(existing.Select(x => x.ParentRoleId)))
            { db.RoleInheritance.Add(new() { ChildRoleId = id, ParentRoleId = added }); details.Add(new() { Kind = AuthorizationChangeKind.Inheritance, RoleId = id, ParentRoleId = added, WasPresent = false, IsPresent = true }); }
        }
        if (details.Count == 0) { await tx.CommitAsync(ct); return; }
        role.ConcurrencyStamp = Guid.NewGuid().ToString();
        await db.SaveChangesAsync(ct);
        await AuthorizationAudit.RecordAsync(db, op, version.Epoch, "Change ordinary role security", details, clock, ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteAsync(string id, string? stamp, AuthorizationVersion version, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await LockAndEvaluate(version, ct);
        using var op = current.Begin("Roles.Delete", new("Role", id), Permissions.Roles.Delete);
        var authority = new AdministrativeAuthority(current);
        AdministrativeAuthority.Require(authority.CanEdit(id), "Only strictly lower ordinary roles may be deleted.");
        var role = await GetRole(id, stamp, ct);
        if (await db.UserRoles.AnyAsync(x => x.RoleId == id, ct)) throw new InvalidOperationException("Remove all user assignments before deleting this role.");
        var edges = await db.RoleInheritance.Where(x => x.ChildRoleId == id || x.ParentRoleId == id).ToListAsync(ct);
        var grants = await db.RolePermissions.Where(x => x.RoleId == id).ToListAsync(ct);
        var after = new RoleGraph(current.Graph.Roles.Values.Where(x => x.Id != id), Grants(current.Graph).Where(x => x.RoleId != id),
            Edges(current.Graph).Where(x => x.ChildRoleId != id && x.ParentRoleId != id));
        authority.ValidateResult(after, id);
        var details = edges.Select(x => new AuthorizationChangeDetail { Kind = AuthorizationChangeKind.Inheritance,
            RoleId = x.ChildRoleId, ParentRoleId = x.ParentRoleId, WasPresent = true, IsPresent = false }).Concat(grants.Select(x => new AuthorizationChangeDetail {
                Kind = AuthorizationChangeKind.Grant, RoleId = id, PermissionKey = x.PermissionKey, WasPresent = true, IsPresent = false })).ToList();
        details.Add(new() { Kind = AuthorizationChangeKind.Role, RoleId = id, WasPresent = true, IsPresent = false, PreviousName = role.Name });
        db.RemoveRange(edges); db.RemoveRange(grants); await db.SaveChangesAsync(ct);
        Check(await roles.DeleteAsync(role));
        await AuthorizationAudit.RecordAsync(db, op, version.Epoch, "Delete ordinary role without reparenting", details, clock, ct);
        await tx.CommitAsync(ct);
    }

    private async Task<AuthorizationEvaluation> LockAndEvaluate(AuthorizationVersion version, CancellationToken ct)
    { await AuthorizationMutationLock.AcquireAsync(db, version, ct); return await permissions.EvaluateCurrentInTransactionAsync(db, ct); }
    private async Task<ApplicationRole> GetRole(string id, string? stamp, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new InvalidOperationException("The role no longer exists.");
        if (string.IsNullOrEmpty(stamp) || role.ConcurrencyStamp != stamp) throw new DbUpdateConcurrencyException("The role changed. Reload the editor.");
        return role;
    }
    internal static IEnumerable<GraphGrant> Grants(RoleGraph graph) => graph.Roles.Keys.SelectMany(id => graph.DirectGrants(id).Select(k => new GraphGrant(id, k)));
    internal static IEnumerable<GraphEdge> Edges(RoleGraph graph) => graph.Roles.Keys.SelectMany(id => graph.DirectParents(id).Select(p => new GraphEdge(id, p)));
    private static string ValidateName(string name) => string.IsNullOrWhiteSpace(name) || name.Trim().Length > 256
        ? throw new ArgumentException("Enter a role name of at most 256 characters.") : name.Trim();
    private static string? ValidateDescription(string? text) => text?.Length > 2000 ? throw new ArgumentException("Description must be at most 2000 characters.")
        : string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static void Check(IdentityResult result)
    { if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(x => x.Description))); }
}
