using Confast.Web.Features.Identity;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    [RoleNavigationBrowserFact]
    public async Task ApplicationNavigationWorksAcrossDesktopTabletAndPhone()
    {
        await WithNavigationBrowserAsync("application-navigation", async (browser, origin, username, password, user, output) =>
        {
            await using var desktop = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 700 } });
            var page = await desktop.NewPageAsync();
            var errors = ObserveErrors(page);
            await LoginForNavigationAsync(page, origin, username, password);
            await CheckDesktopNavigationAsync(page, origin, output);

            foreach (var (width, height) in new[] { (834, 1194), (1366, 1024), (390, 667) })
            {
                await using var tablet = await browser.NewContextAsync(new()
                {
                    ViewportSize = new() { Width = width, Height = height },
                    IsMobile = true,
                    HasTouch = true
                });
                var touchPage = await tablet.NewPageAsync();
                var touchErrors = ObserveErrors(touchPage);
                await LoginForNavigationAsync(touchPage, origin, username, password);
                await CheckDrawerAsync(touchPage, origin, output, width, height);
                Assert.Empty(touchErrors);
            }

            // Apply a real permission change to the disposable installation. The browser app
            // is a separate process and picks up the new authorization epoch during navigation.
            var edit = (await Users.GetUserForEditAsync(user.Id))!;
            Assert.True(edit.Roles.Remove(AppRoles.Administrator));
            Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
            var nav = page.Locator(".app-sidebar nav");
            await NavLink(nav, "Customers").ClickAsync();
            await page.WaitForURLAsync(origin + "/customers");
            await AssertRestrictedNavigationAsync(nav);

            await page.GetByRole(AriaRole.Link, new() { Name = "Log out", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Open navigation", Exact = true }).ClickAsync();
            var staticDrawer = page.GetByRole(AriaRole.Dialog, new() { Name = "Navigation", Exact = true });
            await Category(staticDrawer, "Master Data").ClickAsync();
            await Expect(NavLink(staticDrawer, "Customers")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(staticDrawer).ToBeHiddenAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Log out", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(origin + "/login");
            await Expect(page.Locator("nav[aria-label='Main navigation']")).ToHaveCountAsync(0);
            await LoginForNavigationAsync(page, origin, username, password);
            await AssertRestrictedNavigationAsync(nav);
            Assert.Empty(errors);
        });
    }

    private static async Task CheckDesktopNavigationAsync(IPage page, string origin, string output)
    {
        var sidebar = page.Locator(".app-sidebar");
        var nav = sidebar.GetByRole(AriaRole.Navigation);
        await Expect(nav).ToBeVisibleAsync();
        await Expect(nav.Locator(".navigation-category-toggle")).ToHaveTextAsync(
            ["Production", "Receiving & Logistics", "Quality", "Master Data", "Administration", "Settings"]);
        await Expect(NavLink(nav, "Home")).ToHaveAttributeAsync("aria-current", "page");
        var production = Category(nav, "Production");
        var quality = Category(nav, "Quality");
        var settings = Category(nav, "Settings");
        await Expect(production).ToHaveAttributeAsync("aria-expanded", "false");
        await production.PressAsync("Space");
        await quality.ClickAsync();
        await Expect(production).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(quality).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(nav.Locator("#sidebar-Production a")).ToHaveTextAsync(
            ["Production Tracking", "Production Scheduling", "Production Review"]);

        await NavLink(nav, "Production Tracking").ClickAsync();
        await page.WaitForURLAsync(origin + "/production-tracking");
        await Expect(NavLink(nav, "Production Tracking")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(production).ToHaveClassAsync("navigation-row navigation-category-toggle is-current");
        await Expect(quality).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(settings).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(page.GetByText("Loading production tracking…")).ToBeHiddenAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "desktop-expanded.png") });

        await page.GetByRole(AriaRole.Button, new() { Name = "Collapse sidebar", Exact = true }).ClickAsync();
        await Expect(sidebar).ToHaveClassAsync("app-sidebar app-sidebar-compact");
        await NavLink(nav, "Home").ClickAsync();
        await page.WaitForURLAsync(origin + "/");
        await Expect(sidebar).ToHaveClassAsync("app-sidebar app-sidebar-compact");
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, "desktop-compact.png") });
        await settings.PressAsync("Enter");
        var drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "Navigation", Exact = true });
        var compactNav = drawer.GetByRole(AriaRole.Navigation);
        await Expect(Category(compactNav, "Settings")).ToBeFocusedAsync();
        await Expect(Category(compactNav, "Settings")).ToHaveAttributeAsync("aria-expanded", "true");
        await NavLink(compactNav, "Scheduling Settings").ClickAsync();
        await page.WaitForURLAsync(origin + "/production-scheduling/settings");
        await Expect(drawer).ToBeHiddenAsync();
        await Expect(sidebar).ToHaveClassAsync("app-sidebar app-sidebar-compact");
        await page.GetByRole(AriaRole.Button, new() { Name = "Expand sidebar", Exact = true }).ClickAsync();
        await Expect(settings).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(production).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(NavLink(nav, "Scheduling Settings")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(NavLink(nav, "Production Scheduling")).Not.ToHaveAttributeAsync("aria-current", "page");
        var contentX = (await page.Locator(".app-content").BoundingBoxAsync())!.X;
        Assert.True((await page.Locator(".scheduling-page-heading").BoundingBoxAsync())!.X >= contentX);
        await production.ClickAsync();
        await Expect(production).ToHaveAttributeAsync("aria-expanded", "false");
        await page.GetByRole(AriaRole.Link, new() { Name = "Back to Schedule", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(origin + "/production-scheduling");
        await Expect(production).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(settings).ToHaveAttributeAsync("aria-expanded", "true");

        await page.GotoAsync(origin + "/customers/42/edit/?tab=parts");
        await Expect(Category(nav, "Master Data")).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(NavLink(nav, "Customers")).ToHaveAttributeAsync("aria-current", "location");
        await page.GetByRole(AriaRole.Link, new() { Name = "ConFastDB", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(origin + "/");
        await Expect(Category(nav, "Master Data")).ToHaveAttributeAsync("aria-expanded", "true");

        foreach (var name in new[] { "Production", "Receiving & Logistics", "Quality", "Administration", "Settings" })
            await Category(nav, name).ClickAsync();
        Assert.True(await nav.EvaluateAsync<bool>("el => el.scrollHeight > el.clientHeight"));
        var pageScroll = await page.EvaluateAsync<double>("() => scrollY");
        await nav.EvaluateAsync("el => el.scrollTop = 0");
        await nav.EvaluateAsync("el => el.scrollTop = el.scrollHeight");
        Assert.True(await nav.EvaluateAsync<double>("el => el.scrollTop") > 0);
        Assert.Equal(pageScroll, await page.EvaluateAsync<double>("() => scrollY"));
        await nav.EvaluateAsync("el => el.scrollTop = 0");
    }

    private static async Task CheckDrawerAsync(IPage page, string origin, string output, int width, int height)
    {
        await Expect(page.Locator(".app-sidebar")).ToBeHiddenAsync();
        var trigger = page.Locator("button.drawer-menu-toggle");
        var drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "Navigation", Exact = true });
        var close = drawer.GetByRole(AriaRole.Button, new() { Name = "Close navigation", Exact = true });
        var nav = drawer.GetByRole(AriaRole.Navigation);
        await trigger.TapAsync();
        await Expect(close).ToBeFocusedAsync();
        await Expect(trigger).ToHaveAttributeAsync("aria-expanded", "true");
        Assert.True(await drawer.EvaluateAsync<bool>("element => element.matches(':modal')"));
        var bounds = (await drawer.BoundingBoxAsync())!;
        Assert.InRange(bounds.Width, 240, width - 30);
        Assert.InRange(bounds.Height, height - 1, height + 1);
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await drawer.EvaluateAsync<bool>("element => element.contains(document.activeElement)"));

        await page.Keyboard.PressAsync("Escape");
        await Expect(drawer).ToBeHiddenAsync();
        await Expect(trigger).ToBeFocusedAsync();
        await trigger.TapAsync();
        await page.Touchscreen.TapAsync(width - 12, height / 2);
        await Expect(drawer).ToBeHiddenAsync();
        await Expect(trigger).ToBeFocusedAsync();
        await trigger.TapAsync();
        await close.TapAsync();
        await Expect(drawer).ToBeHiddenAsync();
        await Expect(trigger).ToBeFocusedAsync();

        await trigger.TapAsync();
        foreach (var name in new[] { "Production", "Receiving & Logistics", "Quality", "Master Data", "Administration", "Settings" })
            await Category(nav, name).TapAsync();
        await Expect(Category(nav, "Settings")).ToHaveAttributeAsync("aria-expanded", "true");
        if (height <= 1024)
            Assert.True(await nav.EvaluateAsync<bool>("el => el.scrollHeight > el.clientHeight"));
        await Expect(Category(nav, "Production")).ToHaveAttributeAsync("aria-expanded", "true");
        Assert.True((await NavLink(nav, "Nominal Tolerance").BoundingBoxAsync())!.Height >= 44);
        await nav.EvaluateAsync("el => el.scrollTop = 0");
        await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"drawer-{width}x{height}.png") });
        await NavLink(nav, "Scheduling Settings").TapAsync();
        await page.WaitForURLAsync(origin + "/production-scheduling/settings");
        await Expect(drawer).ToBeHiddenAsync();
        await Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");
        await trigger.TapAsync();
        await Expect(NavLink(nav, "Scheduling Settings")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Category(nav, "Quality")).ToHaveAttributeAsync("aria-expanded", "true");
        await close.TapAsync();
        await Expect(drawer).ToBeHiddenAsync();
    }

    private static async Task LoginForNavigationAsync(IPage page, string origin, string username, string password)
    {
        await page.GotoAsync(origin + "/login");
        await page.GetByLabel("Username", new() { Exact = true }).FillAsync(username);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(origin + "/");
    }

    private static async Task AssertRestrictedNavigationAsync(ILocator nav)
    {
        await Expect(NavLink(nav, "Home")).ToBeVisibleAsync();
        await Expect(nav.Locator(".navigation-category-toggle")).ToHaveTextAsync(
            ["Production", "Receiving & Logistics", "Quality", "Master Data"]);
        await Expect(nav.Locator("a[href='/production-tracking'], a[href='/admin/roles'], a[href='/production-scheduling/settings']"))
            .ToHaveCountAsync(0);
    }

    private static List<string> ObserveErrors(IPage page)
    {
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        return errors;
    }

    private static ILocator Category(ILocator nav, string label) =>
        nav.GetByRole(AriaRole.Button, new() { Name = label, Exact = true });

    private static ILocator NavLink(ILocator nav, string label) =>
        nav.GetByRole(AriaRole.Link, new() { Name = label, Exact = true });
}
