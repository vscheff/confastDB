using System.Security.Cryptography;
using System.Text;

namespace Confast.Web.Features.Authorization;

public sealed record PermissionPath(IReadOnlyList<string> Roles, bool IsEnabled);
public sealed record PermissionSource(string AnchorId, string RoleId, bool IsInherited, bool IsActive,
    IReadOnlyList<PermissionPath> Paths, bool MorePaths, string PathFingerprint, bool GrantRoleEnabled);
public sealed record PermissionMatrixRow(PermissionDefinition Definition, bool Direct, bool Inherited,
    bool Effective, bool Usable, IReadOnlyList<string> MissingPrerequisites,
    IReadOnlyList<PermissionSource> Sources, bool CanAdd, bool CanRemove, string EditReason);
public sealed record PermissionImpact(IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
    IReadOnlyList<string> ProvenanceChanged, IReadOnlyList<string> NewlyUnavailable,
    IReadOnlyList<string> StoredAdded, IReadOnlyList<string> StoredRemoved)
{
    public bool HasChanges => Added.Count + Removed.Count + ProvenanceChanged.Count + NewlyUnavailable.Count
        + StoredAdded.Count + StoredRemoved.Count > 0;
}

// Presentation of the existing graph, never an alternative permission evaluator.
internal static class AdministrationProjection
{
    internal static IReadOnlyList<PermissionMatrixRow> Matrix(RoleGraph graph, IEnumerable<string> assigned,
        bool root = false, bool active = true, AdministrativeAuthority? editor = null, string? editedRole = null)
    {
        var anchors = assigned.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var effective = graph.EvaluateOrdinary(anchors);
        bool Has(string key) => active && (root || effective.ContainsKey(key));
        var canEdit = editor is not null && editedRole is not null && editor.CanEdit(editedRole)
            && PermissionCatalog.ExpandRequirements([Permissions.Roles.ManagePermissions]).All(editor.Actor.Has);
        return PermissionCatalog.All.Select(definition =>
        {
            var sources = new List<PermissionSource>();
            foreach (var anchor in anchors)
                foreach (var role in graph.Ancestors(anchor).Append(anchor).Order(StringComparer.Ordinal))
                {
                    if (!graph.DirectGrants(role).Contains(definition.Key)) continue;
                    var paths = Paths(graph, anchor, role);
                    var operational = active && effective.TryGetValue(definition.Key, out var grants)
                        && grants.Any(x => x.DirectRoleId == anchor && x.GrantRoleId == role);
                    sources.Add(new(anchor, role, anchor != role, operational, paths.Paths, paths.More,
                        PathFingerprint(graph, anchor, role), graph.Roles[role].IsEnabled));
                }
            var direct = sources.Any(x => !x.IsInherited);
            var ordinary = definition.Authority == PermissionAuthority.Ordinary;
            var add = canEdit && ordinary && editor!.Actor.Has(definition.Key);
            var remove = canEdit && ordinary && direct;
            var missing = PermissionCatalog.ExpandRequirements([definition.Key])
                .Where(k => k != definition.Key && !Has(k)).ToArray();
            return new PermissionMatrixRow(definition, direct, sources.Any(x => x.IsInherited && x.IsActive),
                Has(definition.Key), Has(definition.Key) && missing.Length == 0, missing, sources, add, remove,
                !ordinary ? "Protected Root Designation Only" : !canEdit
                    ? "Locked: Requires Strictly Lower Role And Manage Permissions Authority"
                    : add ? "Add And Remove Allowed" : remove ? "Removal Only: You Do Not Hold This Permission"
                    : "Addition Denied: You Do Not Hold This Permission");
        }).ToArray();
    }

    private static string PathFingerprint(RoleGraph graph, string anchor, string source)
    {
        // Fingerprint the complete relevant subgraph. The display path limit must never hide an impact change.
        var nodes = graph.Ancestors(anchor).Append(anchor)
            .Where(id => id == source || graph.Ancestors(id).Contains(source)).ToHashSet(StringComparer.Ordinal);
        var structure = nodes.Order(StringComparer.Ordinal).Select(id => new
        {
            Id = id, graph.Roles[id].IsEnabled,
            Parents = graph.DirectParents(id).Where(nodes.Contains).Order(StringComparer.Ordinal).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(structure))));
    }

    private static (IReadOnlyList<PermissionPath> Paths, bool More) Paths(RoleGraph graph, string anchor, string source)
    {
        const int limit = 12;
        var result = new List<PermissionPath>();
        var path = new List<string>();
        void Visit(string id, bool enabled)
        {
            if (result.Count > limit) return;
            path.Add(id);
            enabled &= graph.Roles[id].IsEnabled;
            if (id == source) result.Add(new(path.ToArray(), enabled));
            else foreach (var parent in graph.DirectParents(id))
                if (parent == source || graph.Ancestors(parent).Contains(source)) Visit(parent, enabled);
            path.RemoveAt(path.Count - 1);
        }
        Visit(anchor, true);
        return (result.Take(limit).ToArray(), result.Count > limit);
    }

    internal static PermissionImpact Compare(RoleGraph before, RoleGraph after, IEnumerable<string> assigned,
        bool active = true, bool root = false)
    {
        var anchors = assigned.ToArray();
        var oldRows = Matrix(before, anchors.Where(before.Roles.ContainsKey), root, active);
        var newRows = Matrix(after, anchors.Where(after.Roles.ContainsKey), root, active);
        return Compare(oldRows, newRows);
    }

    internal static PermissionImpact Compare(IReadOnlyList<PermissionMatrixRow> before, IReadOnlyList<PermissionMatrixRow> after)
    {
        var previous = before.ToDictionary(x => x.Definition.Key);
        string[] Keys(Func<PermissionMatrixRow, PermissionMatrixRow, bool> condition) => after
            .Where(row => condition(previous[row.Definition.Key], row)).Select(x => x.Definition.Key).Order().ToArray();
        static string Provenance(PermissionMatrixRow row) => string.Join(";", row.Sources.Select(s =>
            $"{s.AnchorId}:{s.RoleId}:{s.IsActive}:{s.PathFingerprint}"));
        return new(Keys((a, b) => !a.Effective && b.Effective), Keys((a, b) => a.Effective && !b.Effective),
            Keys((a, b) => a.Effective == b.Effective && Provenance(a) != Provenance(b)),
            Keys((a, b) => a.Usable && !b.Usable),
            Keys((a, b) => a.Sources.Count == 0 && b.Sources.Count > 0),
            Keys((a, b) => a.Sources.Count > 0 && b.Sources.Count == 0));
    }
}
