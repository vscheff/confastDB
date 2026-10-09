using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;

namespace Confast.Web.Tests;

public sealed class PermissionCatalogTests
{
    [Fact]
    public void Manifest_HasExactReviewedClassificationsAndValidPrerequisites()
    {
        Assert.Equal(164, PermissionCatalog.All.Length);
        Assert.Equal(164, PermissionCatalog.ByKey.Count);
        Assert.Equal(106, PermissionCatalog.All.Count(x => x.Kind == PermissionKind.Resource));
        Assert.Equal(58, PermissionCatalog.All.Count(x => x.Kind == PermissionKind.Action));
        Assert.Equal(163, PermissionCatalog.All.Count(x => x.Authority == PermissionAuthority.Ordinary));
        Assert.Equal(Permissions.Authorization.ManageSecurity,
            Assert.Single(PermissionCatalog.All.Where(x => x.Authority == PermissionAuthority.RootOnly)).Key);
        Assert.DoesNotContain("Roles.ManageDelegation", PermissionCatalog.ByKey.Keys);
        Assert.DoesNotContain("Chat.Read", PermissionCatalog.ByKey.Keys);
        Assert.Empty(PermissionCatalog.ByKey[Permissions.Inspections.Create].Prerequisites);
        foreach (var definition in PermissionCatalog.All)
        {
            Assert.All(definition.Prerequisites, key => Assert.True(PermissionCatalog.ByKey.ContainsKey(key)));
            Assert.DoesNotContain(definition.Key, definition.Prerequisites);
            Assert.Contains(definition.Key, PermissionCatalog.ExpandRequirements([definition.Key]));
        }
        Assert.Equal(64, PermissionCatalog.Version.Length);
    }

    [Fact]
    public void Baseline_IsTheExactBusinessReadFloor()
    {
        string[] expected = ["Customers.Read", "Plants.Read", "Parts.Read", "Gages.Read", "GageTypes.Read",
            "InspectionCriteria.Read", "MasterPrints.Read", "Inspections.Read", "Certifications.Read",
            "Suppliers.Read", "Shipments.Read", "Containers.Read", "BillsOfLading.Read", "ProductionSchedules.Read",
            "PlantCertificationRecipients.Read", "PlantCertificationSettings.Read"];
        // The approved manifest, rather than suffix-based seeding, defines the floor.
        Assert.Equal(16, AuthorizationSeeds.ReadOnly.Length);
        Assert.DoesNotContain(Permissions.Chat.Access, AuthorizationSeeds.ReadOnly);
        Assert.Equal(expected.Order(), AuthorizationSeeds.ReadOnly.Order());
        Assert.Equal(AuthorizationSeeds.ReadOnly.Order(), PermissionCatalog.BaselineEnvelope.Order());
        Assert.All(PermissionCatalog.All.Where(x => x.AllowedInBaseline), x =>
        {
            Assert.Equal(PermissionKind.Resource, x.Kind);
            Assert.Equal(PermissionAuthority.Ordinary, x.Authority);
        });
    }

    [Theory]
    [InlineData(AppRoles.ReadOnlyId, 16, 16)]
    [InlineData(AppRoles.QualityId, 69, 85)]
    [InlineData(AppRoles.ProductionId, 61, 77)]
    [InlineData(AppRoles.AdministratorId, 57, 163)]
    public void SeedCounts_AreExplicitDirectAndEffective(string roleId, int direct, int effective)
    {
        var graph = SeedGraph();
        Assert.Equal(direct, graph.DirectGrants(roleId).Count);
        Assert.Equal(effective, graph.EvaluateOrdinary([roleId]).Count);
        Assert.Empty(graph.DirectGrants(AppRoles.RootId));
    }

    [Fact]
    public void HighRiskGrants_AreDeliberateAndAdministratorIsNotRoot()
    {
        var graph = SeedGraph();
        var quality = graph.EvaluateOrdinary([AppRoles.QualityId]);
        var production = graph.EvaluateOrdinary([AppRoles.ProductionId]);
        var admin = graph.EvaluateOrdinary([AppRoles.AdministratorId]);
        Assert.Contains(Permissions.Inspections.Create, quality.Keys);
        Assert.DoesNotContain(Permissions.Inspections.Create, production.Keys);
        Assert.Contains(Permissions.ProductionSchedules.StartWork, production.Keys);
        Assert.DoesNotContain(Permissions.ProductionSchedules.StartWork, quality.Keys);
        foreach (var key in new[] { Permissions.Users.ResetPasswords, Permissions.Roles.ManageInheritance,
                     Permissions.Chat.DeleteOthersMessages, Permissions.Chat.AdministerChannels })
        {
            Assert.Contains(key, admin.Keys);
            Assert.DoesNotContain(key, quality.Keys);
            Assert.DoesNotContain(key, production.Keys);
        }
        Assert.DoesNotContain(Permissions.Authorization.ManageSecurity, admin.Keys);
        Assert.Contains(Permissions.Chat.CreateCategories, quality.Keys);
        Assert.Contains(Permissions.Chat.ReorderChannels, production.Keys);
    }

    internal static RoleGraph SeedGraph() => new(
        AppRoles.Seeds.Select(x => new GraphRole(x.Id, x.SystemKind, x.IsEnabled)),
        AuthorizationSeeds.Grants.Select(x => new GraphGrant(x.RoleId, x.PermissionKey)),
        AuthorizationSeeds.Edges.Select(x => new GraphEdge(x.ChildRoleId, x.ParentRoleId)));
}
