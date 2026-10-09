using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Authorization;

internal static class AuthorizationMutationLock
{
    internal static async Task<AuthorizationState> AcquireAsync(AppDbContext db, AuthorizationVersion expected,
        CancellationToken cancellationToken)
    {
        var state = await AcquireCurrentAsync(db, cancellationToken);
        if (state.InstallationGeneration != expected.Generation || state.CatalogVersion != expected.CatalogVersion
            || state.GlobalEpoch != expected.Epoch)
            throw new DbUpdateConcurrencyException("Authorization state changed. Reload the editor and try again.");
        return state;
    }

    internal static async Task<AuthorizationState> AcquireCurrentAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Authorization changes require an explicit transaction.");
        var state = await db.AuthorizationState.FromSqlRaw("SELECT * FROM authorization_state WHERE id = 1 FOR UPDATE")
            .AsNoTracking().SingleAsync(cancellationToken);
        return state;
    }
}
