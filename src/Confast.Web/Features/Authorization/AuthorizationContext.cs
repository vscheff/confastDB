using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Confast.Web.Features.Authorization;

public enum AuthorizationDenial
{
    Unauthenticated, InvalidSession, InactiveAccount, PendingRoot, InvalidState,
    CatalogMismatch, UnknownPermission, MissingPermission, DatabaseUnavailable, WrongOperation, DelegationDenied
}

public sealed class AuthorizationDeniedException(AuthorizationDenial denial, string? message = null) : Exception(
    message ?? (denial == AuthorizationDenial.PendingRoot
        ? "Permission authorization is awaiting explicit Root provisioning."
        : "This operation is not authorized. Refresh and try again, or contact the installation operator."))
{
    public AuthorizationDenial Denial { get; } = denial;
}

public readonly record struct AuthorizationVersion(Guid Generation, string CatalogVersion, long Epoch);
public sealed record AuthorizationScope(string Area, string? ResourceId = null);

public sealed class EffectivePermissionSnapshot
{
    internal EffectivePermissionSnapshot(string actorId, AuthorizationVersion version, bool isRoot,
        FrozenDictionary<string, ImmutableArray<GrantProvenance>> provenance, string securityStamp)
    {
        ActorUserId = actorId; Version = version; IsRoot = isRoot; Provenance = provenance;
        Permissions = provenance.Keys.ToFrozenSet(StringComparer.Ordinal);
        SecurityStamp = securityStamp;
    }
    public string ActorUserId { get; }
    internal string SecurityStamp { get; }
    public AuthorizationVersion Version { get; }
    public bool IsRoot { get; }
    public FrozenSet<string> Permissions { get; }
    public FrozenDictionary<string, ImmutableArray<GrantProvenance>> Provenance { get; }
    public bool Has(string key) => PermissionCatalog.ByKey.ContainsKey(key) && Permissions.Contains(key);
}

// Internal construction; no deserialization or client DTO can mint an allow context.
public sealed class AuthorizedOperation : IDisposable
{
    private bool completed;
    internal AuthorizedOperation(string purpose, AuthorizationScope scope, ImmutableArray<string> requiredKeys,
        EffectivePermissionSnapshot snapshot)
    {
        Id = Guid.NewGuid(); Purpose = purpose; Scope = scope; RequiredKeys = requiredKeys; Snapshot = snapshot;
    }
    public Guid Id { get; }
    public string Purpose { get; }
    public AuthorizationScope Scope { get; }
    public ImmutableArray<string> RequiredKeys { get; }
    public EffectivePermissionSnapshot Snapshot { get; }
    public string ActorUserId => Snapshot.ActorUserId;

    public void Require(string purpose, AuthorizationScope scope, params string[] keys)
    {
        if (completed || purpose != Purpose || scope != Scope) throw new AuthorizationDeniedException(AuthorizationDenial.WrongOperation);
        foreach (var key in PermissionCatalog.ExpandRequirements(keys))
            if (!Snapshot.Has(key)) throw new AuthorizationDeniedException(AuthorizationDenial.MissingPermission);
    }
    public void Dispose() => completed = true;
}
