using System.Collections.Frozen;
using System.Collections.Immutable;
using Confast.Web.Features.Authorization;

namespace Confast.Web.Tests;

// Cryptographic fixture only; actual service/HTTP authorization tests use database-backed actors.
internal static class PrintTestCapability
{
    internal static AuthorizedOperation Operation(string purpose, long id, params string[] keys)
    {
        var grants = PermissionCatalog.ExpandRequirements(keys).ToFrozenDictionary(x => x, _ => ImmutableArray<GrantProvenance>.Empty);
        return new(purpose, new("Inspection", id.ToString()), keys.ToImmutableArray(),
            new("crypto-fixture", new(Guid.NewGuid(), PermissionCatalog.Version, 1), false, grants, "fixture-stamp"));
    }
}
