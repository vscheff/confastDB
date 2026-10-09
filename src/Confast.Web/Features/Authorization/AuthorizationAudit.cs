using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Authorization;

internal static class AuthorizationAudit
{
    // Future mutation cores must use this enlisted context, not an independent factory/store.
    // The caller must lock state BEFORE authorization/changes and re-evaluate under that lock.
    internal static async Task RecordAsync(AppDbContext db, AuthorizedOperation operation, long previousEpoch,
        string reason, IEnumerable<AuthorizationChangeDetail> details, TimeProvider clock,
        CancellationToken cancellationToken, string? directAnchorRoleId = null)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Security history requires the mutation transaction.");
        operation.Require(operation.Purpose, operation.Scope, operation.RequiredKeys.ToArray());
        var state = await db.AuthorizationState.AsNoTracking().SingleAsync(cancellationToken);
        if (previousEpoch != operation.Snapshot.Version.Epoch
            || state.CatalogVersion != operation.Snapshot.Version.CatalogVersion
            || state.InstallationGeneration != operation.Snapshot.Version.Generation || state.GlobalEpoch <= previousEpoch)
            throw new InvalidOperationException("Authorization mutation version mismatch.");
        db.AuthorizationChangeHistory.Add(new()
        {
            DirectAnchorRoleId = directAnchorRoleId,
            OperationId = operation.Id, ActorKind = AuthorizationActorKind.Human,
            ActorUserId = operation.ActorUserId, Purpose = operation.Purpose, Reason = reason,
            OccurredAtUtc = clock.GetUtcNow().UtcDateTime, PreviousEpoch = previousEpoch,
            ResultingEpoch = state.GlobalEpoch, InstallationGeneration = state.InstallationGeneration,
            Details = details.ToList()
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
