using Confast.Web.Features.Maintenance;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    [RoleNavigationBrowserFact]
    public async Task FactoryResetStaysUnavailableToAnAdministratorOnAnEnabledDeployment()
    {
        await WithNavigationBrowserAsync("factory-reset-authorization", async (browser, origin, username, password, _, _) =>
        {
            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 834, Height = 1194 }, IsMobile = true, HasTouch = true
            });
            var page = await context.NewPageAsync();
            var errors = ObserveErrors(page);
            await LoginForNavigationAsync(page, origin, username, password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Open navigation", Exact = true }).ClickAsync();
            var drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "Navigation", Exact = true });
            await Category(drawer, "Administration").ClickAsync();
            await Expect(NavLink(drawer, "Users")).ToBeVisibleAsync();
            await Expect(NavLink(drawer, "Factory Reset")).ToHaveCountAsync(0);
            await page.GotoAsync(origin + FactoryResetService.Route);
            await page.WaitForURLAsync("**/access-denied?**");
            Assert.Empty(errors);
        }, configure: (_, start) =>
        {
            start.Environment["DatabaseReset__Enabled"] = "true";
            return Task.CompletedTask;
        }, browserChannel: Environment.GetEnvironmentVariable("CONFAST_FACTORY_RESET_BROWSER_CHANNEL"));
    }
}
