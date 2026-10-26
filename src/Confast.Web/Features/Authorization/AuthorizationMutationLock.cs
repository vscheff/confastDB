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
        // Row locking requires UPDATE privilege in PostgreSQL. The fixed-purpose function
        // acquires that lock without granting the runtime login writes to protected state.
        var state = await db.AuthorizationState.FromSqlRaw("SELECT * FROM public.confast_lock_authorization()")
            .AsNoTracking().SingleAsync(cancellationToken);
        return state;
    }
}
