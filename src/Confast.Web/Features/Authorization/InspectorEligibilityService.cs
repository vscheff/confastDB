using System.Data;
using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Authorization;

public sealed record InspectorCandidate(string UserId, string DisplayName, string Username)
{
    public string Label => $"{DisplayName} ({Username})";
}

public sealed class InspectorEligibilityService(IDbContextFactory<AppDbContext> factory, EffectivePermissionService permissions)
{
    public async Task<IReadOnlyList<InspectorCandidate>> GetCandidatesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var current = await permissions.EvaluateCurrentInTransactionAsync(db, ct);
        var key = current.Snapshot.Has(Permissions.Inspections.Create) ? Permissions.Inspections.Create : Permissions.Inspections.Update;
        using var op = current.Begin("Inspections.InspectorChoices", new("Inspectors"), key);
        var rootId = await db.AuthorizationState.Select(x => x.RootUserId).SingleAsync(ct);
        var users = await db.Users.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.DisplayName).ThenBy(x => x.UserName)
            .Select(x => new InspectorCandidate(x.Id, x.DisplayName, x.UserName ?? "")).ToListAsync(ct);
        // One account query and one assignment query, regardless of candidate count.
        var assignments = await db.UserRoles.AsNoTracking().Select(x => new { x.UserId, x.RoleId }).ToListAsync(ct);
        var grouped = assignments.ToLookup(x => x.UserId, x => x.RoleId);
        var result = users.Where(x => x.UserId == rootId || current.Graph.EvaluateOrdinary(grouped[x.UserId])
            .ContainsKey(Permissions.Inspections.Create)).ToArray();
        await tx.CommitAsync(ct);
        return result;
    }

    internal static async Task<string?> ResolveSelectionAsync(AppDbContext db, RoleGraph graph, string? id,
        string? submittedName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id))
        {
            AdministrativeAuthority.Require(string.IsNullOrWhiteSpace(submittedName), "Select an inspector by account identity, not a typed name.");
            return null;
        }
        var user = await db.Users.AsNoTracking().Where(x => x.Id == id && x.IsActive)
            .Select(x => new { x.DisplayName }).SingleOrDefaultAsync(ct);
        AdministrativeAuthority.Require(user is not null, "The selected inspector is no longer active or available.");
        var assigned = await db.UserRoles.AsNoTracking().Where(x => x.UserId == id).Select(x => x.RoleId).ToListAsync(ct);
        var rootId = await db.AuthorizationState.Select(x => x.RootUserId).SingleAsync(ct);
        AdministrativeAuthority.Require(id == rootId || graph.EvaluateOrdinary(assigned).ContainsKey(Permissions.Inspections.Create),
            "The selected inspector no longer has inspection creation permission.");
        return user!.DisplayName;
    }
}
