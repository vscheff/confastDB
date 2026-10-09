using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Security.Claims;
using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Confast.Web.Features.Authorization;

public sealed class EffectivePermissionService(
    IDbContextFactory<AppDbContext> factory,
    AuthenticationStateProvider authenticationState,
    IOptions<IdentityOptions> identityOptions,
    PermissionCache cache,
    ILogger<EffectivePermissionService> logger)
{
    public long SnapshotCount { get; private set; }
    public long ComputationCount { get; private set; }
    public long CacheHitCount { get; private set; }

    // Circuit identity wins; an anonymous circuit cannot borrow an ambient HTTP caller.
    public async Task<EffectivePermissionSnapshot> EvaluateCurrentAsync(CancellationToken cancellationToken = default) =>
        await EvaluateAsync((await authenticationState.GetAuthenticationStateAsync()).User, cancellationToken);

    // Used only by the framework handler/HTTP adapter, never by a client ID or claims DTO.
    internal async Task<EffectivePermissionSnapshot> EvaluateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var claims = identityOptions.Value.ClaimsIdentity;
        var actorId = principal.FindFirstValue(claims.UserIdClaimType);
        var stamp = principal.FindFirstValue(claims.SecurityStampClaimType);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(actorId))
            throw new AuthorizationDeniedException(AuthorizationDenial.Unauthenticated);
        if (string.IsNullOrEmpty(stamp)) throw new AuthorizationDeniedException(AuthorizationDenial.InvalidSession);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var result = await EvaluateCoreAsync(db, actorId, stamp, true, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result.Snapshot;
        }
        catch (AuthorizationDeniedException exception)
        {
            logger.LogWarning("Permission authorization denied: {Reason}", exception.Denial);
            throw;
        }
        catch (NpgsqlException exception)
        {
            logger.LogError(exception, "Authorization primary-database check failed");
            throw new AuthorizationDeniedException(AuthorizationDenial.DatabaseUnavailable);
        }
        finally
        {
            logger.LogDebug("Authorization snapshot completed in {ElapsedMs}ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
    // The caller owns the transaction. Administrative callers take the shared lock first;
    // business callers use their consistent read snapshot. Both use this same evaluator.
    internal async Task<AuthorizationEvaluation> EvaluateCurrentInTransactionAsync(AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        var principal = (await authenticationState.GetAuthenticationStateAsync()).User;
        var claims = identityOptions.Value.ClaimsIdentity;
        if (principal.Identity?.IsAuthenticated != true)
            throw new AuthorizationDeniedException(AuthorizationDenial.Unauthenticated);
        var id = principal.FindFirstValue(claims.UserIdClaimType);
        var stamp = principal.FindFirstValue(claims.SecurityStampClaimType);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(stamp))
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidSession);
        return await EvaluateCoreAsync(db, id, stamp, false, cancellationToken);
    }

    // Provenance is a stored server-issued delegation, never a request-supplied issuer.
    internal Task<AuthorizationEvaluation> EvaluateResetIssuerAsync(AppDbContext db,
        PasswordResetDelegation delegation, CancellationToken cancellationToken) =>
        EvaluateCoreAsync(db, delegation.IssuerUserId, delegation.IssuerSecurityStamp, false, cancellationToken);

    internal async Task<RoleGraph> GraphForOperationAsync(AppDbContext db, AuthorizedOperation operation, CancellationToken ct)
    {
        operation.Require(operation.Purpose, operation.Scope, operation.RequiredKeys.ToArray());
        var state = await db.AuthorizationState.AsNoTracking().SingleAsync(ct);
        var version = new AuthorizationVersion(state.InstallationGeneration, state.CatalogVersion, state.GlobalEpoch);
        if (version != operation.Snapshot.Version)
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidState, "Inspector authorization changed. Reload and try again.");
        var graph = cache.GetGraph(version);
        if (graph is not null) return graph;
        return new RoleGraph(await db.Roles.AsNoTracking().Select(x => new GraphRole(x.Id, x.SystemKind, x.IsEnabled)).ToListAsync(ct),
            await db.RolePermissions.AsNoTracking().Select(x => new GraphGrant(x.RoleId, x.PermissionKey)).ToListAsync(ct),
            await db.RoleInheritance.AsNoTracking().Select(x => new GraphEdge(x.ChildRoleId, x.ParentRoleId)).ToListAsync(ct));
    }

    private async Task<AuthorizationEvaluation> EvaluateCoreAsync(AppDbContext db, string actorId,
        string stamp, bool useCache, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Authorization evaluation requires a consistent transaction.");
        SnapshotCount++;
        var current = await (from state in db.AuthorizationState.AsNoTracking()
            from actor in db.Users.AsNoTracking().Where(x => x.Id == actorId)
            where state.Id == 1
            select new
            {
                actor.IsActive, actor.SecurityStamp, state.RootUserId, state.Readiness, state.BaselineRoleId,
                state.RootRoleId, state.GlobalEpoch, state.InstallationGeneration, state.CatalogVersion,
                HasBaseline = db.UserRoles.Any(x => x.UserId == actorId && x.RoleId == AppRoles.ReadOnlyId),
                RootMembers = db.UserRoles.Count(x => x.RoleId == AppRoles.RootId),
                MatchingRoot = db.UserRoles.Any(x => x.RoleId == AppRoles.RootId && x.UserId == state.RootUserId),
                RootAccountValid = db.Users.Any(x => x.Id == state.RootUserId && x.IsActive && x.PasswordHash != null && x.PasswordHash != ""),
                ProtectedRoles = db.Roles.Count(x =>
                    x.Id == AppRoles.ReadOnlyId && x.SystemKind == SystemRoleKind.Baseline && x.SystemKey == "ReadOnlyBaseline"
                    && x.Name == AppRoles.ReadOnly && x.NormalizedName == "READONLY" && x.IsEnabled
                    || x.Id == AppRoles.RootId && x.SystemKind == SystemRoleKind.Root && x.SystemKey == "RootAdministratorPrime"
                    && x.Name == AppRoles.Root && x.NormalizedName == "ROOT ADMINISTRATOR PRIME" && x.IsEnabled),
                SystemRoles = db.Roles.Count(x => x.SystemKind != SystemRoleKind.Ordinary),
                ManifestCount = db.Permissions.Count(),
                WrongManifestVersion = db.Permissions.Any(x => x.CatalogVersion != PermissionCatalog.Version)
            }).SingleOrDefaultAsync(cancellationToken);
        if (current is null) throw new AuthorizationDeniedException(AuthorizationDenial.InvalidState);
        if (!current.IsActive) throw new AuthorizationDeniedException(AuthorizationDenial.InactiveAccount);
        if (!string.Equals(current.SecurityStamp, stamp, StringComparison.Ordinal))
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidSession);
        if (current.BaselineRoleId != AppRoles.ReadOnlyId || current.RootRoleId != AppRoles.RootId
            || current.ProtectedRoles != 2 || current.SystemRoles != 2 || !current.HasBaseline
            || current.InstallationGeneration == Guid.Empty || current.GlobalEpoch < 0)
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidState);
        if (current.CatalogVersion != PermissionCatalog.Version || current.ManifestCount != PermissionCatalog.All.Length
            || current.WrongManifestVersion)
            throw new AuthorizationDeniedException(AuthorizationDenial.CatalogMismatch);
        if (current.Readiness == AuthorizationReadiness.PendingRoot)
        {
            if (current.RootUserId is not null || current.RootMembers != 0)
                throw new AuthorizationDeniedException(AuthorizationDenial.InvalidState);
            throw new AuthorizationDeniedException(AuthorizationDenial.PendingRoot);
        }
        if (current.Readiness != AuthorizationReadiness.Ready || current.RootUserId is null
            || !current.RootAccountValid || current.RootMembers != 1 || !current.MatchingRoot)
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidState);

        var version = new AuthorizationVersion(current.InstallationGeneration, current.CatalogVersion, current.GlobalEpoch);
        var hit = useCache ? cache.GetActor(version, actorId) : null;
        var graph = cache.GetGraph(version);
        if (graph is null)
        {
            var manifest = await db.Permissions.AsNoTracking().ToListAsync(cancellationToken);
            if (manifest.Any(x => !PermissionCatalog.ByKey.TryGetValue(x.Key, out var definition)
                || definition.DisplayName != x.DisplayName || definition.Category != x.Category
                || definition.Kind != x.Kind || definition.Authority != x.Authority || definition.AllowedInBaseline != x.AllowedInBaseline))
                throw new AuthorizationDeniedException(AuthorizationDenial.CatalogMismatch);
            var roles = await db.Roles.AsNoTracking().Select(x => new GraphRole(x.Id, x.SystemKind, x.IsEnabled)).ToListAsync(cancellationToken);
            var grants = await db.RolePermissions.AsNoTracking().Select(x => new GraphGrant(x.RoleId, x.PermissionKey)).ToListAsync(cancellationToken);
            var edges = await db.RoleInheritance.AsNoTracking().Select(x => new GraphEdge(x.ChildRoleId, x.ParentRoleId)).ToListAsync(cancellationToken);
            try { graph = new RoleGraph(roles, grants, edges); }
            catch (InvalidOperationException) { throw new AuthorizationDeniedException(AuthorizationDenial.InvalidState); }
            cache.PutGraph(version, graph);
        }
        var assignments = await db.UserRoles.AsNoTracking().Where(x => x.UserId == actorId)
            .Select(x => x.RoleId).ToListAsync(cancellationToken);
        if (hit is not null)
        {
            CacheHitCount++;
            return new(hit, graph, assignments);
        }
        var isRoot = current.RootUserId == actorId;
        var provenance = isRoot
            ? PermissionCatalog.All.ToFrozenDictionary(x => x.Key,
                _ => ImmutableArray.Create(new GrantProvenance(AppRoles.RootId, AppRoles.RootId, false)), StringComparer.Ordinal)
            : graph.EvaluateOrdinary(assignments);
        ComputationCount++;
        var snapshot = new EffectivePermissionSnapshot(actorId, version, isRoot, provenance, stamp);
        if (useCache) cache.PutActor(snapshot);
        return new(snapshot, graph, assignments);
    }

}

internal sealed record AuthorizationEvaluation(EffectivePermissionSnapshot Snapshot, RoleGraph Graph, IReadOnlyList<string> DirectRoleIds)
{
    internal AuthorizedOperation Begin(string purpose, AuthorizationScope scope, params string[] keys)
    {
        var required = PermissionCatalog.ExpandRequirements(keys);
        if (!required.All(Snapshot.Has)) throw new AuthorizationDeniedException(AuthorizationDenial.MissingPermission);
        return new(purpose, scope, required, Snapshot);
    }
}
