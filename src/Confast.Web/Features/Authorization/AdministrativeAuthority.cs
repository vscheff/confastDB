using Confast.Web.Features.Identity;

namespace Confast.Web.Features.Authorization;

// All calculations come from the existing evaluator's locked snapshot and RoleGraph.
internal sealed class AdministrativeAuthority(AuthorizationEvaluation current)
{
    internal EffectivePermissionSnapshot Actor => current.Snapshot;
    internal RoleGraph Graph => current.Graph;
    private IEnumerable<string> Held => current.DirectRoleIds.Where(id => Graph.Roles[id].Kind == SystemRoleKind.Ordinary);
    internal IEnumerable<string> Anchors => Held.Where(id => Graph.Roles[id].IsEnabled);

    internal bool CanAssign(string roleId, bool adding) => Graph.Roles.TryGetValue(roleId, out var role)
        && role.Kind == SystemRoleKind.Ordinary && (!adding || role.IsEnabled)
        && (Actor.IsRoot || Anchors.Any(a => Graph.Position(a, roleId) is RolePosition.Same or RolePosition.Lower)
            && Graph.StoredEnvelope(roleId).IsSubsetOf(Actor.Permissions));

    internal bool CanEdit(string roleId) => Graph.Roles.TryGetValue(roleId, out var role)
        && role.Kind == SystemRoleKind.Ordinary
        && (Actor.IsRoot || !Held.Contains(roleId) && Anchors.Any(a => Graph.Position(a, roleId) == RolePosition.Lower));

    internal void RequireTarget(string targetId, IEnumerable<string> assigned, string? rootId)
    {
        Require(targetId != rootId, "Root account settings require the protected security workflow.");
        Require(targetId != Actor.ActorUserId, "Use your dedicated self-profile/security workflow to change your own account.");
        var roles = assigned.ToArray();
        Require(!roles.Contains(AppRoles.RootId), "The protected Root account cannot be administered here.");
        Require(Actor.IsRoot || Graph.StoredUserEnvelope(roles).IsSubsetOf(Actor.Permissions)
            && roles.Where(id => Graph.Roles[id].Kind == SystemRoleKind.Ordinary).All(id => CanAssign(id, false)),
            "This account has higher, unrelated, or dormant authority outside your management scope.");
    }

    internal void RequireAssignment(string targetId, string? rootId, IEnumerable<string> before, IEnumerable<string> after)
    {
        Require(targetId != rootId && targetId != Actor.ActorUserId,
            "You cannot change your own administrative roles or the protected Root account's roles.");
        var old = before.ToHashSet(StringComparer.Ordinal);
        var next = after.ToHashSet(StringComparer.Ordinal);
        Require(next.Contains(AppRoles.ReadOnlyId), "ReadOnly is permanent and cannot be removed.");
        Require(!next.Contains(AppRoles.RootId) && !old.Contains(AppRoles.RootId), "Root cannot be assigned or removed here.");
        foreach (var id in old.Except(next)) Require(CanAssign(id, false), "You cannot remove a higher or unrelated role, or its dormant grants.");
        foreach (var id in next.Except(old)) Require(CanAssign(id, true), "Select an enabled peer or lower role within your current permissions.");
    }

    internal void ValidateResult(RoleGraph after, string targetId)
    {
        if (Actor.IsRoot) return;
        if (after.Roles.ContainsKey(targetId))
            Require(after.StoredEnvelope(targetId).IsSubsetOf(Actor.Permissions), "The resulting role retains permissions outside your authority.");
        var affected = Graph.AffectedDescendants(targetId).Append(targetId).ToHashSet();
        if (after.Roles.ContainsKey(targetId)) affected.UnionWith(after.AffectedDescendants(targetId));
        foreach (var id in affected.Where(after.Roles.ContainsKey))
        {
            var previously = Graph.Roles.ContainsKey(id) ? Graph.StoredEnvelope(id) : [];
            Require(after.StoredEnvelope(id).Except(previously).All(Actor.Has), "The edit propagates unauthorized grants to a descendant.");
        }
        Require(after.EvaluateOrdinary(current.DirectRoleIds).Keys.All(Actor.Has), "The edit would increase your own permissions.");
        // Check each anchor independently, including disabled directly held roles.
        // Restricting to old IDs permits only the fresh empty subordinate created by Roles.Create.
        foreach (var id in Held)
            Require(after.Ancestors(id).Where(Graph.Roles.ContainsKey).All(Graph.Ancestors(id).Contains),
                "The edit would expand a directly held role's management reach over existing roles.");
    }

    internal static void Require(bool allowed, string message)
    {
        if (!allowed) throw new AuthorizationDeniedException(AuthorizationDenial.DelegationDenied, message);
    }
}
