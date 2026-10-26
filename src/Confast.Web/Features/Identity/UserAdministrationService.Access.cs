using System.Data;
using Confast.Web.Features.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Identity;

public sealed record UserAccessRole(string Id, string Name, bool Enabled, IReadOnlyList<string> Parents);
public sealed record UserAccessView(AuthorizationVersion Version, string? ConcurrencyStamp, bool IsRoot, bool IsActive,
    IReadOnlyList<UserAccessRole> Roles, IReadOnlyList<PermissionMatrixRow> Matrix, PermissionImpact? Impact,
    bool Permitted, string? Rejection, IReadOnlyDictionary<string, string> RoleNames);

public sealed partial class UserAdministrationService
{
    public async Task<UserAccessView> GetEffectiveAccessAsync(string userId, IReadOnlyCollection<string>? proposedRoleNames = null,
        AuthorizationVersion? version = null, string? concurrencyStamp = null, CancellationToken ct = default)
    {
        await using var read = await factory.CreateDbContextAsync(ct);
        await using var tx = await read.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var current = await permissions.EvaluateCurrentInTransactionAsync(read, ct);
        using var op = current.Begin("Users.EffectiveAccess", new("User", userId), Permissions.Users.Read);
        var target = await read.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new { x.IsActive, x.ConcurrencyStamp }).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The user no longer exists.");
        if (version is not null && (version != current.Snapshot.Version || concurrencyStamp != target.ConcurrencyStamp))
            throw new DbUpdateConcurrencyException("Authorization or account state changed. Reload and reapply before previewing.");
        var rootId = await read.AuthorizationState.Select(x => x.RootUserId).SingleAsync(ct);
        var names = await read.Roles.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name ?? "", ct);
        var assigned = await read.UserRoles.Where(x => x.UserId == userId).Select(x => x.RoleId).ToArrayAsync(ct);
        var next = assigned;
        string? rejection = null;
        if (proposedRoleNames is not null)
        {
            op.Require(op.Purpose, op.Scope, Permissions.Users.ManageRoles);
            next = names.Where(x => proposedRoleNames.Contains(x.Value)).Select(x => x.Key).ToArray();
            if (next.Length != proposedRoleNames.Distinct(StringComparer.Ordinal).Count())
                throw new ArgumentException("A submitted role no longer exists. Reload the editor.");
            try { new AdministrativeAuthority(current).RequireAssignment(userId, rootId, assigned, next); }
            catch (AuthorizationDeniedException ex) { rejection = ex.Message; }
        }
        var graph = current.Graph;
        var matrix = AdministrationProjection.Matrix(graph, next, userId == rootId, target.IsActive);
        var result = new UserAccessView(current.Snapshot.Version, userId == rootId ? null : target.ConcurrencyStamp,
            userId == rootId, target.IsActive,
            next.Select(id => new UserAccessRole(id, names[id], graph.Roles[id].IsEnabled,
                graph.DirectParents(id).Select(p => names[p]).ToArray())).ToArray(), matrix,
            proposedRoleNames is null ? null : AdministrationProjection.Compare(
                AdministrationProjection.Matrix(graph, assigned, userId == rootId, target.IsActive), matrix),
            rejection is null, rejection, names);
        await tx.CommitAsync(ct);
        return result;
    }
}
