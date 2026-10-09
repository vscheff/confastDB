using Microsoft.Extensions.Caching.Memory;

namespace Confast.Web.Features.Authorization;

// Computed data only. Every use is preceded by a fresh primary-database version/account read.
public sealed class PermissionCache : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 4096 });
    public RoleGraph? GetGraph(AuthorizationVersion version) => cache.Get<RoleGraph>(("graph", version));
    public EffectivePermissionSnapshot? GetActor(AuthorizationVersion version, string id) =>
        cache.Get<EffectivePermissionSnapshot>(("actor", version, id));
    public void PutGraph(AuthorizationVersion version, RoleGraph graph) =>
        cache.Set(("graph", version), graph, Options(Math.Max(1, graph.Roles.Count)));
    public void PutActor(EffectivePermissionSnapshot snapshot) =>
        cache.Set(("actor", snapshot.Version, snapshot.ActorUserId), snapshot, Options(1));
    private static MemoryCacheEntryOptions Options(int size) => new MemoryCacheEntryOptions()
        .SetSize(size).SetSlidingExpiration(TimeSpan.FromMinutes(15));
    public void Dispose() => cache.Dispose();
}
