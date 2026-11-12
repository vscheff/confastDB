using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Confast.Web.Tests;

public sealed class NavigationCatalogTests
{
    [Fact]
    public void AllMenuDestinationsAndTheirPoliciesMatchExistingRoutes()
    {
        var expected = new[]
        {
            "/", "/production-tracking", "/production-scheduling", "/morning-production-review",
            "/received-parts", "/container-tracking", "/inspections", "/gages", "/gage-types",
            "/parts", "/customers", "/suppliers", "/admin/users", "/admin/roles",
            "/production-scheduling/settings", "/production-tracking/settings",
            "/admin/certification-email-templates", "/admin/certification-email-settings", "/admin/nominal-tolerance"
        };
        var items = NavigationCatalog.Destinations.Prepend(NavigationCatalog.Home).ToArray();
        Assert.Equal(expected, items.Select(item => item.Route));
        Assert.Equal(items.Length, items.Select(item => item.Id).Distinct().Count());

        var routes = typeof(Program).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>()
                .Select(route => (route.Template, Type: type)))
            .ToDictionary(route => route.Template, route => route.Type);
        foreach (var item in items)
        {
            Assert.Contains(routes[item.Route].GetCustomAttributes<AuthorizeAttribute>(),
                attribute => attribute.Policy == item.AuthorizationPolicy);
            Assert.All(item.AnyPermissionKeys, key => Assert.Contains(key, PermissionCatalog.ByKey.Keys));
        }
        Assert.False(NavigationCatalog.Home.ShowOnHome);
        Assert.All(NavigationCatalog.Destinations, item => Assert.True(item.ShowOnHome));
    }

    [Fact]
    public void RootSeesOrderedGroupsWithGageTypesAvailableThroughGages()
    {
        var permissions = Snapshot(true, PermissionCatalog.All.Select(definition => definition.Key).ToArray());
        var groups = NavigationCatalog.GetVisibleGroups(permissions);

        Assert.Collection(groups,
            group => CheckGroup(group, "Production", "Production Tracking", "Production Scheduling", "Production Review"),
            group => CheckGroup(group, "Receiving & Logistics", "Received Parts", "Container Tracking"),
            group => CheckGroup(group, "Quality", "Inspections", "Gages"),
            group => CheckGroup(group, "Master Data", "Parts", "Customers", "Suppliers"),
            group => CheckGroup(group, "Administration", "Users", "Roles"),
            group => CheckGroup(group, "Settings", "Scheduling Settings", "Sort Log Downtime Causes",
                "Email Templates", "Email Settings", "Nominal Tolerance"));
        Assert.Equal(NavigationCatalog.Home, NavigationCatalog.FindActive("/", groups));
    }

    [Fact]
    public void RestrictedUsersSeeOnlyPermittedDestinationsAndHaveAnAuthorizedHome()
    {
        var permissions = Snapshot(false, Permissions.GageTypes.Read, Permissions.ProductionSettings.Read);
        var groups = NavigationCatalog.GetVisibleGroups(permissions);

        Assert.Collection(groups,
            group => CheckGroup(group, "Quality", "Gage Types"),
            group => CheckGroup(group, "Settings", "Scheduling Settings"));
        Assert.Equal(NavigationCatalog.Home, NavigationCatalog.FindActive("/", groups));
        Assert.Equal("gage-types", NavigationCatalog.FindActive("/gage-types", groups)?.Id);
        Assert.Null(NavigationCatalog.FindActive("/production-scheduling", groups));
        Assert.Null(NavigationCatalog.FindActive("/admin/users", groups));

        var noPermissions = Snapshot(false);
        Assert.Empty(NavigationCatalog.GetVisibleGroups(noPermissions));
        Assert.Equal(NavigationCatalog.Home, NavigationCatalog.FindActive("/", []));
        Assert.Empty(NavigationCatalog.GetVisibleGroups(null));
    }

    [Theory]
    [InlineData("/?q=example", "home")]
    [InlineData("/customers/?q=example#list", "customers")]
    [InlineData("/CUSTOMERS/42/edit/?tab=parts", "customers")]
    [InlineData("/parts/12/inspection-criteria/4/criteria/5/edit", "parts")]
    [InlineData("/container-tracking/shipments/7/containers/create", "container-tracking")]
    [InlineData("/inspections/7/print", "inspections")]
    [InlineData("/gage-types", "gages")]
    [InlineData("/production-scheduling/settings/", "scheduling-settings")]
    [InlineData("/production-tracking/settings?tab=causes", "downtime-causes")]
    [InlineData("/production-scheduling", "production-scheduling")]
    [InlineData("/customers-extra/42/edit", null)]
    [InlineData("/production-scheduling/archive", null)]
    [InlineData("/access-denied", null)]
    public void ResolvesActiveDestinations(string route, string? expected)
    {
        var permissions = Snapshot(true, PermissionCatalog.All.Select(definition => definition.Key).ToArray());
        var groups = NavigationCatalog.GetVisibleGroups(permissions);
        var path = NavigationCatalog.GetPath(new Uri("https://confast.example" + route));
        Assert.Equal(expected, NavigationCatalog.FindActive(path, groups)?.Id);
    }

    private static void CheckGroup(NavigationGroup group, string label, params string[] items)
    {
        Assert.Equal(label, group.Category.Label);
        Assert.Equal(items, group.Items.Select(item => item.Label));
        Assert.False(string.IsNullOrWhiteSpace(group.Category.AccentClass));
    }

    private static EffectivePermissionSnapshot Snapshot(bool isRoot, params string[] keys) =>
        new("test-user", new(Guid.Empty, PermissionCatalog.Version, 0), isRoot,
            keys.ToFrozenDictionary(key => key, _ => ImmutableArray<GrantProvenance>.Empty, StringComparer.Ordinal), "test-stamp");
}
