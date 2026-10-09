using System.Collections.Frozen;
using System.Collections.Immutable;
using Confast.Web.Features.Identity;

namespace Confast.Web.Features.Authorization;

public sealed record GraphRole(string Id, SystemRoleKind Kind, bool IsEnabled);
public sealed record GraphGrant(string RoleId, string PermissionKey);
public sealed record GraphEdge(string ChildRoleId, string ParentRoleId);
public sealed record GrantProvenance(string DirectRoleId, string GrantRoleId, bool IsInherited);
public enum RolePosition { Same, Lower, Higher, Unrelated, Protected }

public sealed class RoleGraph
{
    public FrozenDictionary<string, GraphRole> Roles { get; }
    private readonly FrozenDictionary<string, ImmutableArray<string>> parents;
    private readonly FrozenDictionary<string, FrozenSet<string>> directGrants;
    private readonly FrozenDictionary<string, FrozenSet<string>> closures;

    public RoleGraph(IEnumerable<GraphRole> roles, IEnumerable<GraphGrant> grants, IEnumerable<GraphEdge> edges)
    {
        Roles = roles.ToFrozenDictionary(x => x.Id, StringComparer.Ordinal);
        if (Roles.Values.Any(x => !Enum.IsDefined(x.Kind))) throw new InvalidOperationException("Unsupported role kind.");
        if (!Roles.TryGetValue(AppRoles.ReadOnlyId, out var baseline) || baseline.Kind != SystemRoleKind.Baseline
            || !baseline.IsEnabled || !Roles.TryGetValue(AppRoles.RootId, out var root)
            || root.Kind != SystemRoleKind.Root || !root.IsEnabled
            || Roles.Values.Count(x => x.Kind == SystemRoleKind.Baseline) != 1
            || Roles.Values.Count(x => x.Kind == SystemRoleKind.Root) != 1)
            throw new InvalidOperationException("Invalid protected role identities.");

        var grantRows = grants.ToArray();
        if (grantRows.Distinct().Count() != grantRows.Length) throw new InvalidOperationException("Duplicate grant.");
        foreach (var grant in grantRows)
        {
            if (!Roles.TryGetValue(grant.RoleId, out var role)
                || !PermissionCatalog.ByKey.TryGetValue(grant.PermissionKey, out var permission)
                || role.Kind == SystemRoleKind.Root || permission.Authority != PermissionAuthority.Ordinary
                || role.Kind == SystemRoleKind.Baseline && !permission.AllowedInBaseline)
                throw new InvalidOperationException("Invalid stored grant.");
        }
        directGrants = Roles.Keys.ToFrozenDictionary(x => x,
            x => grantRows.Where(g => g.RoleId == x).Select(g => g.PermissionKey).ToFrozenSet(StringComparer.Ordinal));

        var edgeRows = edges.ToArray();
        if (edgeRows.Distinct().Count() != edgeRows.Length) throw new InvalidOperationException("Duplicate inheritance edge.");
        foreach (var edge in edgeRows)
        {
            if (edge.ChildRoleId == edge.ParentRoleId || !Roles.TryGetValue(edge.ChildRoleId, out var child)
                || !Roles.TryGetValue(edge.ParentRoleId, out var parent)
                || child.Kind != SystemRoleKind.Ordinary || parent.Kind == SystemRoleKind.Root)
                throw new InvalidOperationException("Invalid inheritance edge.");
        }
        parents = Roles.Keys.ToFrozenDictionary(x => x,
            x => edgeRows.Where(e => e.ChildRoleId == x).Select(e => e.ParentRoleId).Order(StringComparer.Ordinal).ToImmutableArray());
        var completed = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        FrozenSet<string> Close(string id)
        {
            if (completed.TryGetValue(id, out var previous)) return previous;
            if (!visiting.Add(id)) throw new InvalidOperationException("Role inheritance cycle.");
            var reached = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parent in parents[id])
            {
                reached.Add(parent);
                reached.UnionWith(Close(parent));
            }
            visiting.Remove(id);
            return completed[id] = reached.ToFrozenSet(StringComparer.Ordinal);
        }
        foreach (var id in Roles.Keys) Close(id);
        closures = completed.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public ImmutableArray<string> DirectParents(string roleId) => parents[roleId];
    public FrozenSet<string> DirectGrants(string roleId) => directGrants[roleId];
    public FrozenSet<string> Ancestors(string roleId) => closures[roleId];
    public FrozenSet<string> AffectedDescendants(string roleId) =>
        closures.Where(x => x.Value.Contains(roleId)).Select(x => x.Key).ToFrozenSet(StringComparer.Ordinal);

    public RolePosition Position(string anchorRoleId, string targetRoleId)
    {
        if (Roles[anchorRoleId].Kind != SystemRoleKind.Ordinary || Roles[targetRoleId].Kind != SystemRoleKind.Ordinary)
            return RolePosition.Protected;
        if (anchorRoleId == targetRoleId) return RolePosition.Same;
        if (closures[anchorRoleId].Contains(targetRoleId)) return RolePosition.Lower;
        return closures[targetRoleId].Contains(anchorRoleId) ? RolePosition.Higher : RolePosition.Unrelated;
    }

    // Dormant authority is deliberately retained for Phase 3C management safeguards.
    public FrozenSet<string> StoredEnvelope(string roleId) => closures[roleId].Append(roleId)
        .SelectMany(x => directGrants[x]).ToFrozenSet(StringComparer.Ordinal);
    public FrozenSet<string> StoredUserEnvelope(IEnumerable<string> assignedRoleIds) => assignedRoleIds
        .Where(x => Roles[x].Kind == SystemRoleKind.Ordinary).SelectMany(StoredEnvelope).ToFrozenSet(StringComparer.Ordinal);

    public FrozenDictionary<string, ImmutableArray<GrantProvenance>> EvaluateOrdinary(IEnumerable<string> assignedRoleIds)
    {
        var provenance = new Dictionary<string, HashSet<GrantProvenance>>(StringComparer.Ordinal);
        foreach (var anchor in assignedRoleIds.Append(AppRoles.ReadOnlyId).Distinct(StringComparer.Ordinal))
        {
            if (!Roles.ContainsKey(anchor)) throw new InvalidOperationException("Unknown assigned role.");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string id)
            {
                if (!visited.Add(id) || !Roles[id].IsEnabled || Roles[id].Kind == SystemRoleKind.Root) return;
                foreach (var key in directGrants[id])
                {
                    if (!provenance.TryGetValue(key, out var sources)) provenance[key] = sources = [];
                    sources.Add(new(anchor, id, anchor != id));
                }
                foreach (var parent in parents[id]) Visit(parent);
            }
            Visit(anchor);
        }
        return provenance.ToFrozenDictionary(x => x.Key,
            x => x.Value.OrderBy(p => p.DirectRoleId, StringComparer.Ordinal)
                .ThenBy(p => p.GrantRoleId, StringComparer.Ordinal).ToImmutableArray(), StringComparer.Ordinal);
    }
}
