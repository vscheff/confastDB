using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Navigation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    [RoleNavigationBrowserFact]
    public async Task HomeQuickAccessWorksAcrossSessionsAndTouchLayouts()
    {
        await WithNavigationBrowserAsync("home-quick-access", async (browser, origin, username, password, user, output) =>
        {
            await using var desktop = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
            var page = await desktop.NewPageAsync();
            var errors = ObserveErrors(page);
            await LoginForNavigationAsync(page, origin, username, password);
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Quick Access", Exact = true })).ToBeVisibleAsync();
            await AssertDepartmentLayoutAsync(page, 3);
            await Expect(page.Locator(".shortcut-link")).ToHaveCountAsync(8);
            await Expect(page.Locator(".shortcut-link").First).ToHaveAccessibleDescriptionAsync("Suggested shortcut");

            var options = ShortcutOptions(page, "Production Tracking");
            await options.PressAsync("Enter");
            var menu = page.GetByRole(AriaRole.Dialog, new() { Name = "Production Tracking", Exact = true });
            var pin = menu.GetByRole(AriaRole.Button, new() { Name = "Pin", Exact = true });
            await Expect(pin).ToBeFocusedAsync();
            Assert.Equal(origin + "/", page.Url);
            await page.Keyboard.PressAsync("Escape");
            await Expect(menu).ToBeHiddenAsync();
            await Expect(options).ToBeFocusedAsync();

            var add = page.GetByRole(AriaRole.Button, new() { Name = "Add shortcut", Exact = true });
            await add.ClickAsync();
            var picker = page.GetByRole(AriaRole.Dialog, new() { Name = "Add shortcut", Exact = true });
            var search = picker.GetByRole(AriaRole.Searchbox);
            await Expect(search).ToBeFocusedAsync();
            await search.FillAsync("roles");
            await picker.GetByRole(AriaRole.Button, new() { Name = "Roles", Exact = true }).ClickAsync();
            await Expect(picker).ToBeHiddenAsync();
            await Expect(page.Locator(".shortcut-link").First).ToHaveTextAsync("Roles");

            await options.ClickAsync();
            await pin.ClickAsync();
            await Expect(menu).ToBeHiddenAsync();
            await Expect(page.Locator(".shortcut-link").Nth(1)).ToHaveAccessibleDescriptionAsync("Pinned shortcut");
            await options.PressAsync("Space");
            await menu.GetByRole(AriaRole.Button, new() { Name = "Move earlier", Exact = true }).PressAsync("Enter");
            await Expect(menu).ToBeHiddenAsync();
            await Expect(page.Locator(".shortcut-link").First).ToHaveTextAsync("Production Tracking");

            await options.ClickAsync();
            await menu.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
            await Expect(menu).ToBeHiddenAsync();
            await Expect(add).ToBeFocusedAsync();
            await Expect(page.Locator(".shortcut-link[href='/production-tracking']")).ToHaveCountAsync(0);

            var manage = page.GetByRole(AriaRole.Button, new() { Name = "Manage", Exact = true });
            await manage.ClickAsync();
            var settings = page.GetByRole(AriaRole.Dialog, new() { Name = "Manage Quick Access", Exact = true });
            var capacity = settings.GetByLabel("Shortcut slots");
            await capacity.SelectOptionAsync("6");
            await Expect(page.Locator(".shortcut-link")).ToHaveCountAsync(6);
            await settings.GetByRole(AriaRole.Button, new() { Name = "Restore Production Tracking", Exact = true }).ClickAsync();
            await Expect(page.Locator(".shortcut-link[href='/production-tracking']")).ToHaveCountAsync(1);
            await Expect(capacity).ToBeFocusedAsync();
            await capacity.SelectOptionAsync("12");
            await Expect(page.Locator(".shortcut-link")).ToHaveCountAsync(12);
            await page.Keyboard.PressAsync("Escape");
            await Expect(settings).ToBeHiddenAsync();
            await Expect(manage).ToBeFocusedAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "home-desktop.png"), FullPage = true });

            foreach (var (width, height, columns) in new[] { (834, 1194, 2), (1366, 1024, 3), (390, 667, 1) })
            {
                await using var touch = await browser.NewContextAsync(new()
                {
                    ViewportSize = new() { Width = width, Height = height }, HasTouch = true, IsMobile = true,
                    ReducedMotion = ReducedMotion.Reduce
                });
                var device = await touch.NewPageAsync();
                var touchErrors = ObserveErrors(device);
                await LoginForNavigationAsync(device, origin, username, password);
                await AssertDepartmentLayoutAsync(device, columns);
                await Expect(device.Locator(".shortcut-link")).ToHaveCountAsync(12);
                await Expect(device.Locator(".shortcut-link").First).ToHaveTextAsync("Roles");
                var touchOptions = ShortcutOptions(device, "Roles");
                Assert.True((await touchOptions.BoundingBoxAsync())!.Height >= 44);
                await touchOptions.TapAsync();
                var touchMenu = device.GetByRole(AriaRole.Dialog, new() { Name = "Roles", Exact = true });
                await Expect(touchMenu.GetByRole(AriaRole.Button, new() { Name = "Unpin", Exact = true })).ToBeFocusedAsync();
                var bounds = (await touchMenu.BoundingBoxAsync())!;
                Assert.InRange(bounds.X, 0, width - bounds.Width);
                Assert.InRange(bounds.Y, 0, height - bounds.Height);
                await touchMenu.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).TapAsync();
                await Expect(touchOptions).ToBeFocusedAsync();
                await device.ScreenshotAsync(new() { Path = Path.Combine(output, $"home-{width}x{height}.png"), FullPage = true });
                Assert.Empty(touchErrors);
            }

            foreach (var item in NavigationCatalog.ShortcutDestinations.Take(6))
            {
                await ShortcutOptions(page, item.Label).ClickAsync();
                var actions = page.GetByRole(AriaRole.Dialog, new() { Name = item.Label, Exact = true });
                await actions.GetByRole(AriaRole.Button, new() { Name = "Pin", Exact = true }).ClickAsync();
                await Expect(actions).ToBeHiddenAsync();
            }
            await manage.ClickAsync();
            await capacity.SelectOptionAsync("6");
            await Expect(settings.GetByRole(AriaRole.Alert)).ToContainTextAsync("7 saved pins");
            await Expect(capacity).ToHaveValueAsync("12");
            await page.Keyboard.PressAsync("Escape");
            await Expect(settings).ToBeHiddenAsync();

            var edit = (await Users.GetUserForEditAsync(user.Id))!;
            Assert.True(edit.Roles.Remove(AppRoles.Administrator));
            Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
            await page.ReloadAsync();
            await Expect(page.Locator(".department-card h3")).ToHaveTextAsync(
                ["Production", "Receiving & Logistics", "Quality", "Master Data"]);
            await Expect(page.Locator("main a[href='/admin/roles']")).ToHaveCountAsync(0);
            await add.ClickAsync();
            await Expect(picker).ToBeVisibleAsync();
            await picker.GetByRole(AriaRole.Searchbox).FillAsync("Roles");
            await Expect(picker.GetByRole(AriaRole.Button, new() { Name = "Roles", Exact = true })).ToHaveCountAsync(0);
            await page.Keyboard.PressAsync("Escape");
            await Expect(picker).ToBeHiddenAsync();
            await manage.ClickAsync();
            await Expect(settings.GetByText("Some saved pins", new() { Exact = false })).ToBeVisibleAsync();
            await using (var db = database.CreateDbContext())
                Assert.True(await db.Set<NavigationShortcutPreference>().AnyAsync(pin => pin.UserId == user.Id && pin.DestinationId == "roles"));

            await settings.GetByRole(AriaRole.Button, new() { Name = "Reset Quick Access", Exact = true }).ClickAsync();
            var confirm = page.GetByRole(AriaRole.Dialog, new() { Name = "Reset Quick Access?", Exact = true });
            var cancel = confirm.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true });
            await Expect(cancel).ToBeFocusedAsync();
            await cancel.ClickAsync();
            await Expect(capacity).ToHaveValueAsync("12");
            await settings.GetByRole(AriaRole.Button, new() { Name = "Reset Quick Access", Exact = true }).ClickAsync();
            await confirm.GetByRole(AriaRole.Button, new() { Name = "Reset preferences", Exact = true }).ClickAsync();
            await Expect(confirm).ToBeHiddenAsync();
            await manage.ClickAsync();
            await Expect(capacity).ToHaveValueAsync("8");
            Assert.Empty(errors);
        });
    }

    [RoleNavigationBrowserFact]
    public async Task HomePreservesLoginDeepLinksAndRecordsLogicalPageVisits()
    {
        var customer = new Customer { Name = "Quick Access browser fixture" };
        await using (var db = database.CreateDbContext())
        {
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        await WithNavigationBrowserAsync("home-activity", async (browser, origin, username, password, user, _) =>
        {
            await using var desktop = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
            var page = await desktop.NewPageAsync();
            var errors = ObserveErrors(page);
            var bookmark = $"/customers/{customer.Id}/edit?search=sample-query";
            await page.GotoAsync(origin + bookmark);
            Assert.StartsWith(origin + "/login?", page.Url);
            await page.GetByLabel("Username", new() { Exact = true }).FillAsync(username);
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(origin + bookmark);
            var customerHeading = page.GetByRole(AriaRole.Heading, new() { Name = customer.Name, Exact = true });
            await Expect(customerHeading).ToBeVisibleAsync();
            Assert.Equal(1, (await WaitForVisitAsync(user.Id, "customers")).VisitCount);
            await page.ReloadAsync();
            await Expect(customerHeading).ToBeVisibleAsync();
            Assert.Equal(origin + bookmark, page.Url);

            // Move the stored timestamp beyond the database throttle, so a duplicate
            // write from a rerender or a query-only navigation would actually count.
            var previous = DateTimeOffset.UtcNow.AddMinutes(-1);
            await using (var db = database.CreateDbContext())
                await db.Set<NavigationUsage>().Where(row => row.UserId == user.Id && row.DestinationId == "customers")
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.LastVisitedUtc, previous));
            var tab = page.GetByRole(AriaRole.Tab, new() { Name = "Parts", Exact = true });
            await tab.ClickAsync();
            await Expect(tab).ToHaveClassAsync("subtab subtab-active");
            var newQuery = $"/customers/{customer.Id}/edit?search=another-query";
            await page.EvaluateAsync("path => Blazor.navigateTo(path)", newQuery);
            await page.WaitForURLAsync(origin + newQuery);
            await Expect(customerHeading).ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Link, new() { Name = "ConFastDB", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(origin + "/");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Quick Access", Exact = true })).ToBeVisibleAsync();
            var parts = page.Locator(".department-card a[href='/parts']");
            await parts.ClickAsync();
            await page.WaitForURLAsync(origin + "/parts");
            await WaitForVisitAsync(user.Id, "parts");
            Assert.Equal(1, (await WaitForVisitAsync(user.Id, "customers")).VisitCount);
            await page.EvaluateAsync("() => Blazor.navigateTo('/customers/missing-route')");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Not Found", Exact = true })).ToBeVisibleAsync();
            Assert.Equal(1, (await WaitForVisitAsync(user.Id, "customers")).VisitCount);

            var edit = (await Users.GetUserForEditAsync(user.Id))!;
            Assert.True(edit.Roles.Remove(AppRoles.Administrator));
            Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
            await page.GotoAsync(origin + "/");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Quick Access", Exact = true })).ToBeVisibleAsync();
            await page.EvaluateAsync("() => Blazor.navigateTo('/customers/create')");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Log in", Exact = true })).ToBeVisibleAsync();

            await using var verify = database.CreateDbContext();
            var recorded = await verify.Set<NavigationUsage>().Where(row => row.UserId == user.Id)
                .OrderBy(row => row.DestinationId).ToArrayAsync();
            Assert.Equal(new[] { "customers", "parts" }, recorded.Select(row => row.DestinationId));
            Assert.All(recorded, row => Assert.Equal(1, row.VisitCount));
            Assert.Empty(errors);
        });
    }

    private static ILocator ShortcutOptions(IPage page, string label) =>
        page.GetByRole(AriaRole.Button, new() { Name = $"Options for {label}", Exact = true });

    private static async Task AssertDepartmentLayoutAsync(IPage page, int columns)
    {
        var cards = page.Locator(".department-card");
        await Expect(cards.Locator("h3")).ToHaveTextAsync([.. NavigationCatalog.Categories.Select(category => category.Label)]);
        Assert.Equal(columns, await page.Locator(".department-grid").EvaluateAsync<int>(
            "grid => getComputedStyle(grid).gridTemplateColumns.split(' ').length"));
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        for (var i = 0; i < NavigationCatalog.Categories.Length; i++)
        {
            var category = NavigationCatalog.Categories[i];
            var card = cards.Nth(i);
            var menu = page.Locator($"#sidebar-{category.Id}-toggle");
            Assert.Equal(await menu.Locator(":scope > svg path").GetAttributeAsync("d"),
                await card.Locator(".department-icon svg path").GetAttributeAsync("d"));
            Assert.Equal(await menu.EvaluateAsync<string>("el => getComputedStyle(el).getPropertyValue('--navigation-accent')"),
                await card.EvaluateAsync<string>("el => getComputedStyle(el).getPropertyValue('--navigation-accent')"));
            await Expect(card.Locator("a")).ToHaveTextAsync(await page.Locator($"#sidebar-{category.Id} a").AllTextContentsAsync());
        }
    }

    private async Task<NavigationUsage> WaitForVisitAsync(string userId, string id)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await using var db = database.CreateDbContext();
            var visit = await db.Set<NavigationUsage>().SingleOrDefaultAsync(row => row.UserId == userId && row.DestinationId == id);
            if (visit is not null) return visit;
            await Task.Delay(100);
        }

        throw new InvalidOperationException($"No activity was recorded for {id}.");
    }
}
