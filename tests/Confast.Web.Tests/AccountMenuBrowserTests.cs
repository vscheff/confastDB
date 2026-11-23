using Confast.Web.Features.Identity;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    [RoleNavigationBrowserFact]
    public async Task AccountMenuShowsTheCurrentProfilePictureWithoutDependingOnChatAccess()
    {
        await WithNavigationBrowserAsync("account-menu", async (browser, origin, username, password, user, _) =>
        {
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
            var page = await context.NewPageAsync();
            var picturePath = origin + "/account/profile-picture";
            var anonymous = await context.APIRequest.GetAsync(picturePath, new() { MaxRedirects = 0 });
            Assert.Contains(anonymous.Status, new[] { 302, 401 });

            await LoginForNavigationAsync(page, origin, username, password);
            var menu = AccountMenu(page);
            var avatar = menu.Locator(".user-menu-avatar");
            await Expect(avatar).ToHaveTextAsync("BT");
            await Expect(menu).ToHaveCSSAsync("border-top-width", "0px");
            await Expect(menu.Locator("svg")).ToHaveCountAsync(0);

            await menu.PressAsync("Enter");
            await page.Keyboard.PressAsync("Tab");
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Appearance", Exact = true })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(menu).ToBeFocusedAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Open chat", Exact = true }).ClickAsync();
            await page.Locator(".chat-profile-toggle").ClickAsync();
            await page.Locator("#chat-profile-picture-input").SetInputFilesAsync(new FilePayload
            {
                Name = "avatar.gif",
                MimeType = "image/gif",
                Buffer = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7")
            });
            var editor = page.GetByRole(AriaRole.Dialog, new() { Name = "Edit profile picture" });
            await editor.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }).ClickAsync();
            await Expect(editor).ToBeHiddenAsync();
            const string loadedAvatar = ".user-menu-avatar img";
            await page.WaitForFunctionAsync("selector => document.querySelector(selector)?.naturalWidth > 0", loadedAvatar);

            var edit = (await Users.GetUserForEditAsync(user.Id))!;
            Assert.True(edit.Roles.Remove(AppRoles.Administrator));
            Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
            await page.ReloadAsync();
            await page.WaitForFunctionAsync("selector => document.querySelector(selector)?.naturalWidth > 0", loadedAvatar);
            var picture = await context.APIRequest.GetAsync(picturePath);
            Assert.True(picture.Ok);
            Assert.Equal("private, no-store", picture.Headers["cache-control"]);
            Assert.Equal(403, (await context.APIRequest.GetAsync($"{origin}/profile-pictures/{user.Id}")).Status);
        });
    }
}
