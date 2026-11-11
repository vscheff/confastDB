using System.Collections.Immutable;
using Confast.Web.Features.Authorization;

namespace Confast.Web.Features.Navigation;

public enum NavigationCategoryId { Production, Receiving, Quality, MasterData, Administration, Settings }

public enum NavigationIconName
{
    Home, Factory, Package, ClipboardCheck, Database, Shield, Settings,
    Tracking, Calendar, Report, Truck, Gage, Parts, Customers, Suppliers, Users, Mail, Ruler,
    Menu, Chevron, Close
}

public sealed record NavigationCategory(NavigationCategoryId Id, string Label, NavigationIconName Icon, string AccentClass, int Order);

public sealed record NavigationItem(string Id, string Label, string Route, NavigationCategoryId? Category,
    NavigationIconName Icon, int Order, string AuthorizationPolicy, ImmutableArray<string> AnyPermissionKeys,
    bool ShowOnHome = true, bool MatchDescendants = false, string? FallbackFor = null);

public sealed record NavigationGroup(NavigationCategory Category, ImmutableArray<NavigationItem> Items);

public static class NavigationCatalog
{
    private const string GagesId = "gages";

    public static NavigationItem Home { get; } =
        Page("home", "Home", "/", null, NavigationIconName.Home, 0, Permissions.Customers.Read, showOnHome: false);

    public static ImmutableArray<NavigationCategory> Categories { get; } =
    [
        new(NavigationCategoryId.Production, "Production", NavigationIconName.Factory, "navigation-accent-production", 10),
        new(NavigationCategoryId.Receiving, "Receiving & Logistics", NavigationIconName.Package, "navigation-accent-receiving", 20),
        new(NavigationCategoryId.Quality, "Quality", NavigationIconName.ClipboardCheck, "navigation-accent-quality", 30),
        new(NavigationCategoryId.MasterData, "Master Data", NavigationIconName.Database, "navigation-accent-master-data", 40),
        new(NavigationCategoryId.Administration, "Administration", NavigationIconName.Shield, "navigation-accent-slate", 50),
        new(NavigationCategoryId.Settings, "Settings", NavigationIconName.Settings, "navigation-accent-slate", 60)
    ];

    public static ImmutableArray<NavigationItem> Destinations { get; } =
    [
        Page("production-tracking", "Production Tracking", "/production-tracking", NavigationCategoryId.Production,
            NavigationIconName.Tracking, 10, Permissions.ProductionLogs.Read),
        Page("production-scheduling", "Production Scheduling", "/production-scheduling", NavigationCategoryId.Production,
            NavigationIconName.Calendar, 20, Permissions.ProductionSchedules.Read),
        Page("production-review", "Production Review", "/morning-production-review", NavigationCategoryId.Production,
            NavigationIconName.Report, 30, Permissions.ProductionReports.Read),
        Page("received-parts", "Received Parts", "/received-parts", NavigationCategoryId.Receiving,
            NavigationIconName.Package, 10, Permissions.Receiving.Read),
        Page("container-tracking", "Container Tracking", "/container-tracking", NavigationCategoryId.Receiving,
            NavigationIconName.Truck, 20, Permissions.Shipments.Read, matchDescendants: true),
        Page("inspections", "Inspections", "/inspections", NavigationCategoryId.Quality,
            NavigationIconName.ClipboardCheck, 10, Permissions.Inspections.Read, matchDescendants: true),
        Page(GagesId, "Gages", "/gages", NavigationCategoryId.Quality, NavigationIconName.Gage, 20, Permissions.Gages.Read),
        Page("gage-types", "Gage Types", "/gage-types", NavigationCategoryId.Quality,
            NavigationIconName.Gage, 30, Permissions.GageTypes.Read, fallbackFor: GagesId),
        Page("parts", "Parts", "/parts", NavigationCategoryId.MasterData,
            NavigationIconName.Parts, 10, Permissions.Parts.Read, matchDescendants: true),
        Page("customers", "Customers", "/customers", NavigationCategoryId.MasterData,
            NavigationIconName.Customers, 20, Permissions.Customers.Read, matchDescendants: true),
        Page("suppliers", "Suppliers", "/suppliers", NavigationCategoryId.MasterData,
            NavigationIconName.Suppliers, 30, Permissions.Suppliers.Read),
        Page("users", "Users", "/admin/users", NavigationCategoryId.Administration,
            NavigationIconName.Users, 10, Permissions.Users.Read),
        Page("roles", "Roles", "/admin/roles", NavigationCategoryId.Administration,
            NavigationIconName.Shield, 20, Permissions.Roles.Read),
        new("scheduling-settings", "Scheduling Settings", "/production-scheduling/settings", NavigationCategoryId.Settings,
            NavigationIconName.Calendar, 10, PermissionPolicies.ProductionConfigurationRead, [.. PermissionPolicies.ProductionConfigurationReadKeys]),
        Page("downtime-causes", "Sort Log Downtime Causes", "/production-tracking/settings", NavigationCategoryId.Settings,
            NavigationIconName.Settings, 20, Permissions.SortLogDowntimeCauses.Read),
        Page("email-templates", "Email Templates", "/admin/certification-email-templates", NavigationCategoryId.Settings,
            NavigationIconName.Mail, 30, Permissions.CertificationEmailTemplates.Read),
        Page("email-settings", "Email Settings", "/admin/certification-email-settings", NavigationCategoryId.Settings,
            NavigationIconName.Mail, 40, Permissions.CertificationEmailSettings.Read),
        Page("nominal-tolerance", "Nominal Tolerance", "/admin/nominal-tolerance", NavigationCategoryId.Settings,
            NavigationIconName.Ruler, 50, Permissions.NominalToleranceSettings.Read)
    ];

    public static ImmutableArray<NavigationGroup> GetVisibleGroups(EffectivePermissionSnapshot? permissions)
    {
        if (permissions is null)
            return [];

        var authorized = Destinations.Where(item => CanAccess(item, permissions)).ToArray();
        return [.. Categories.OrderBy(category => category.Order)
            .Select(category => new NavigationGroup(category,
                [.. authorized.Where(item => item.Category == category.Id
                    && !authorized.Any(other => other.Id == item.FallbackFor))
                    .OrderBy(item => item.Order)]))
            .Where(group => !group.Items.IsEmpty)];
    }

    public static string? GetHomeRoute(EffectivePermissionSnapshot? permissions, ImmutableArray<NavigationGroup> groups) =>
        permissions is not null && CanAccess(Home, permissions)
            ? Home.Route
            : groups.FirstOrDefault()?.Items[0].Route;

    public static string GetPath(Uri location) => location.AbsolutePath.TrimEnd('/') is { Length: > 0 } path ? path : "/";

    public static bool IsExact(NavigationItem item, string path) =>
        path.Equals(item.Route, StringComparison.OrdinalIgnoreCase);

    public static NavigationItem? FindActive(string path, ImmutableArray<NavigationGroup> groups, string? homeRoute)
    {
        if (homeRoute == Home.Route && IsExact(Home, path))
            return Home;

        // Match the catalog before filtering so a hidden route cannot light up another entry.
        var matched = Destinations.FirstOrDefault(item => IsExact(item, path))
            ?? Destinations.Where(item => item.MatchDescendants
                && path.StartsWith(item.Route + "/", StringComparison.OrdinalIgnoreCase))
                .MaxBy(item => item.Route.Length);

        return matched is null ? null : groups.SelectMany(group => group.Items)
            .FirstOrDefault(item => item.Id == matched.Id || item.Id == matched.FallbackFor);
    }

    private static bool CanAccess(NavigationItem item, EffectivePermissionSnapshot permissions) =>
        item.AnyPermissionKeys.Any(key => PermissionCatalog.ExpandRequirements([key]).All(permissions.Has));

    private static NavigationItem Page(string id, string label, string route, NavigationCategoryId? category,
        NavigationIconName icon, int order, string key, bool showOnHome = true, bool matchDescendants = false,
        string? fallbackFor = null) =>
        new(id, label, route, category, icon, order, AuthorizationRegistration.PolicyName(key), [key],
            showOnHome, matchDescendants, fallbackFor);
}
