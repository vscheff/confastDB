namespace Confast.Web.Features.Authorization;

// Intent is kept apart from the last authoritative projection so conflicts cannot silently erase edits.
public sealed class RoleEditDraft(RoleEditorInput baseline)
{
    public RoleEditorInput Baseline { get; private set; } = baseline;
    public string Name { get; set; } = baseline.Name;
    public string? Description { get; set; } = baseline.Description;
    public bool IsEnabled { get; set; } = baseline.IsEnabled;
    public HashSet<string> Grants { get; private set; } = baseline.Grants.ToHashSet(StringComparer.Ordinal);
    public HashSet<string> Parents { get; private set; } = baseline.Parents.ToHashSet(StringComparer.Ordinal);
    public bool IsDirty => Name != Baseline.Name || Description != Baseline.Description || IsEnabled != Baseline.IsEnabled
        || !Grants.SetEquals(Baseline.Grants) || !Parents.SetEquals(Baseline.Parents);
    public RoleEditorInput Input(bool delete = false) => new(Baseline.Id, Name, Description, IsEnabled,
        Grants.Order().ToArray(), Parents.Order().ToArray(), Baseline.ConcurrencyStamp, Baseline.Version, delete);
    public void Reapply(RoleEditorInput latest)
    {
        if (latest.Id != Baseline.Id) throw new ArgumentException("Cannot reapply edits to another role.");
        if (Name == Baseline.Name) Name = latest.Name;
        if (Description == Baseline.Description) Description = latest.Description;
        if (IsEnabled == Baseline.IsEnabled) IsEnabled = latest.IsEnabled;
        static HashSet<string> Merge(IEnumerable<string> latestValues, IEnumerable<string> previous, HashSet<string> proposed)
        {
            var merged = latestValues.ToHashSet(StringComparer.Ordinal);
            merged.ExceptWith(previous.Except(proposed));
            merged.UnionWith(proposed.Except(previous));
            return merged;
        }
        Grants = Merge(latest.Grants, Baseline.Grants, Grants);
        Parents = Merge(latest.Parents, Baseline.Parents, Parents);
        Baseline = latest;
    }
}
