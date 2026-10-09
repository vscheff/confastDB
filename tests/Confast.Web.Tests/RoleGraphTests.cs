using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;

namespace Confast.Web.Tests;

public sealed class RoleGraphTests
{
    private static RoleGraph Graph(GraphEdge[] edges, GraphGrant[]? grants = null, string? disabled = null) => new(
        AppRoles.Seeds.Select(x => new GraphRole(x.Id, x.SystemKind, x.IsEnabled))
            .Concat(new[] { "a", "b", "c", "d" }.Select(x => new GraphRole(x, SystemRoleKind.Ordinary, x != disabled))),
        grants ?? [new("a", Permissions.Users.Read), new("b", Permissions.Users.Create),
            new("c", Permissions.Users.Create), new("d", Permissions.Users.Update)], edges);

    [Fact]
    public void DiamondAndTransitiveInheritance_DeduplicatesAndPreservesSources()
    {
        var graph = Graph([new("a", "b"), new("a", "c"), new("b", "d"), new("c", "d")]);
        var result = graph.EvaluateOrdinary(["a"]);
        Assert.Equal(3, result.Count);
        Assert.False(Assert.Single(result[Permissions.Users.Read]).IsInherited);
        Assert.Equal(new[] { "b", "c" }, result[Permissions.Users.Create].Select(x => x.GrantRoleId));
        Assert.Equal(new GrantProvenance("a", "d", true), Assert.Single(result[Permissions.Users.Update]));
        Assert.Equal(RolePosition.Lower, graph.Position("a", "d"));
        Assert.Equal(RolePosition.Higher, graph.Position("d", "a"));
        Assert.Equal(RolePosition.Unrelated, graph.Position("b", "c"));
        Assert.Equal(RolePosition.Same, graph.Position("a", "a"));
        Assert.Equal(new[] { "a", "b", "c" }, graph.AffectedDescendants("d").Order());
    }

    [Fact]
    public void MultipleDirectAssignments_PreserveDirectAndInheritedProvenance()
    {
        var graph = Graph([new("a", "b"), new("b", "d")]);
        var sources = graph.EvaluateOrdinary(["a", "b"])[Permissions.Users.Create];
        Assert.Contains(new GrantProvenance("a", "b", true), sources);
        Assert.Contains(new GrantProvenance("b", "b", false), sources);
        Assert.Equal(2, sources.Length);
    }

    [Fact]
    public void DisabledBranch_StopsOperationalTraversalButRetainsDormantEnvelope()
    {
        var graph = Graph([new("a", "b"), new("b", "d")], disabled: "b");
        Assert.Single(graph.EvaluateOrdinary(["a"]));
        Assert.Empty(graph.EvaluateOrdinary(["b"]));
        Assert.Equal(3, graph.StoredEnvelope("a").Count);
        Assert.Equal(2, graph.StoredUserEnvelope(["b"]).Count);
        Assert.Equal(RolePosition.Lower, graph.Position("a", "d"));
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData(AppRoles.RootId, "a")]
    [InlineData("a", AppRoles.RootId)]
    [InlineData(AppRoles.ReadOnlyId, "a")]
    public void ProtectedAndSelfEdges_AreRejected(string child, string parent) =>
        Assert.Throws<InvalidOperationException>(() => Graph([new(child, parent)]));

    [Fact]
    public void CyclesAndDuplicates_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Graph([new("a", "b"), new("b", "a")]));
        Assert.Throws<InvalidOperationException>(() => Graph([new("a", "b"), new("b", "c"), new("c", "a")]));
        Assert.Throws<InvalidOperationException>(() => Graph([new("a", "b"), new("a", "b")]));
        Assert.Throws<InvalidOperationException>(() => Graph([], [new("a", Permissions.Users.Read), new("a", Permissions.Users.Read)]));
    }

    [Theory]
    [InlineData("a", "Unknown.Permission")]
    [InlineData("a", Permissions.Authorization.ManageSecurity)]
    [InlineData(AppRoles.RootId, Permissions.Users.Read)]
    [InlineData(AppRoles.ReadOnlyId, Permissions.Chat.Access)]
    [InlineData(AppRoles.ReadOnlyId, Permissions.Parts.Update)]
    public void InvalidGrants_AreRejected(string role, string permission) =>
        Assert.Throws<InvalidOperationException>(() => Graph([], [new(role, permission)]));

    [Fact]
    public void Prerequisites_AreCheckedAtUseAndUnknownKeysAlwaysDeny()
    {
        Assert.Equal(new[] { Permissions.Chat.Access, Permissions.Chat.ScheduleMessages, Permissions.Chat.SendMessages }.Order(),
            PermissionCatalog.ExpandRequirements([Permissions.Chat.ScheduleMessages]).Order());
        Assert.Equal(AuthorizationDenial.UnknownPermission,
            Assert.Throws<AuthorizationDeniedException>(() => PermissionCatalog.ExpandRequirements(["Unknown.Permission"])).Denial);
    }
}
