using Confast.Web.Features.Chat;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Parts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using PdfSharp.Pdf;
using static Microsoft.Playwright.Assertions;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    [RoleNavigationBrowserFact]
    public async Task AppearancePersistsAndFollowsSystemAcrossDesktopAndTablet()
    {
        await WithNavigationBrowserAsync("appearance", async (browser, origin, username, password, _, output) =>
        {
            await using var desktop = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 1440, Height = 940 },
                ColorScheme = ColorScheme.Light
            });
            var page = await desktop.NewPageAsync();
            var errors = ObserveErrors(page);
            await desktop.AddInitScriptAsync("""
                window.themeDocument = {};
                document.addEventListener('DOMContentLoaded', () => {
                    const root = document.documentElement;
                    window.initialTheme = [root.dataset.appearanceMode, root.dataset.theme, root.dataset.accentColor];
                }, { once: true });
                """);
            await LoginForNavigationAsync(page, origin, username, password);
            var root = page.Locator("html");
            await Expect(root).ToHaveAttributeAsync("data-theme", "light");
            await Expect(root).ToHaveAttributeAsync("data-appearance-mode", "system");
            await Expect(root).ToHaveAttributeAsync("data-accent-color", "#176B9C");
            await page.EvaluateAsync("() => window.themeDocument.marker = 'original'");

            var menu = AccountMenu(page);
            await menu.PressAsync("Enter");
            await page.Keyboard.PressAsync("Tab");
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Appearance", Exact = true })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(menu).ToBeFocusedAsync();
            await OpenAppearanceAsync(page);
            await Expect(ModeButton(page, "System")).ToHaveAttributeAsync("aria-pressed", "true");
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
            await Expect(root).ToHaveAttributeAsync("data-theme", "dark");
            await Expect(ModeButton(page, "System")).ToHaveAttributeAsync("aria-pressed", "true");
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await Expect(root).ToHaveAttributeAsync("data-theme", "light");

            await ModeButton(page, "Dark").ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-appearance-mode", "dark");
            await Expect(root).ToHaveAttributeAsync("data-theme", "dark");
            var statusColor = await BackgroundAsync(page.Locator(".preview-table .status-active"));
            var departmentColor = await page.Locator(".app-sidebar .navigation-accent-quality .navigation-icon").First
                .EvaluateAsync<string>("icon => getComputedStyle(icon).color");
            var originalHeader = await BackgroundAsync(page.Locator("header.app-header"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Purple", Exact = true }).ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-accent-color", "#7C3AED");
            Assert.NotEqual(originalHeader, await BackgroundAsync(page.Locator("header.app-header")));
            Assert.Equal(statusColor, await BackgroundAsync(page.Locator(".preview-table .status-active")));
            Assert.Equal(departmentColor, await page.Locator(".app-sidebar .navigation-accent-quality .navigation-icon").First
                .EvaluateAsync<string>("icon => getComputedStyle(icon).color"));

            var hex = page.GetByLabel("HEX color", new() { Exact = true });
            var apply = page.GetByRole(AriaRole.Button, new() { Name = "Apply custom color", Exact = true });
            var preview = page.Locator("[data-theme-preview]");
            await hex.FillAsync("#12xz");
            await apply.ClickAsync();
            Assert.False(await hex.EvaluateAsync<bool>("field => field.validity.valid"));
            await Expect(root).ToHaveAttributeAsync("data-accent-color", "#7C3AED");
            await hex.FillAsync("#ff0");
            await Expect(preview).ToHaveAttributeAsync("data-preview-accent", "#FFFF00");
            await Expect(root).ToHaveAttributeAsync("data-accent-color", "#7C3AED");
            await apply.ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-accent-color", "#FFFF00");
            await Expect(page.Locator("[data-theme-preview][data-preview-accent]")).ToHaveCountAsync(0);
            await Expect(hex).ToHaveValueAsync("#FFFF00");
            await page.GetByLabel("Custom color", new() { Exact = true }).FillAsync("#faf0ff");
            await Expect(hex).ToHaveValueAsync("#FAF0FF");
            await Expect(preview).ToHaveAttributeAsync("data-preview-accent", "#FAF0FF");
            await apply.ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-accent-color", "#FAF0FF");
            Assert.Equal("original", await page.EvaluateAsync<string>("() => window.themeDocument.marker"));

            var reconnect = page.Locator("#components-reconnect-modal");
            await desktop.SetOfflineAsync(true);
            try
            {
                await Expect(reconnect).ToBeVisibleAsync(new() { Timeout = 45000 });
                await Expect(root).ToHaveAttributeAsync("data-theme", "dark");
            }
            finally { await desktop.SetOfflineAsync(false); }
            await Expect(reconnect).ToBeHiddenAsync(new() { Timeout = 30000 });
            await ModeButton(page, "System").ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-theme", "light");
            await Expect(root).ToHaveAttributeAsync("data-appearance-mode", "system");
            Assert.Equal("original", await page.EvaluateAsync<string>("() => window.themeDocument.marker"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "appearance-light-desktop.png") });

            await using var tablet = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 834, Height = 1194 },
                IsMobile = true, HasTouch = true, ColorScheme = ColorScheme.Dark
            });
            var touch = await tablet.NewPageAsync();
            var touchErrors = ObserveErrors(touch);
            var home = await touch.GotoAsync(origin + "/");
            Assert.Contains("/login", home!.Url);
            await LoginForNavigationAsync(touch, origin, username, password);
            await Expect(touch.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            await Expect(touch.Locator("html")).ToHaveAttributeAsync("data-accent-color", "#FAF0FF");
            await AccountMenu(touch).TapAsync();
            await touch.GetByRole(AriaRole.Link, new() { Name = "Appearance", Exact = true }).TapAsync();
            await Expect(ModeButton(touch, "System")).ToHaveAttributeAsync("aria-pressed", "true");
            Assert.True(await touch.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            Assert.True((await touch.GetByRole(AriaRole.Button, new() { Name = "Teal", Exact = true }).BoundingBoxAsync())!.Height >= 44);
            await touch.ScreenshotAsync(new() { Path = Path.Combine(output, "appearance-dark-ipad.png") });

            await AccountMenu(touch).TapAsync();
            await touch.GetByRole(AriaRole.Button, new() { Name = "Switch to light mode", Exact = true }).TapAsync();
            await Expect(touch.Locator("html")).ToHaveAttributeAsync("data-appearance-mode", "light");
            await Expect(ModeButton(touch, "Light")).ToHaveAttributeAsync("aria-pressed", "true");
            await touch.Keyboard.PressAsync("Escape");
            await touch.GetByRole(AriaRole.Button, new() { Name = "Teal", Exact = true }).TapAsync();
            await Expect(touch.Locator("html")).ToHaveAttributeAsync("data-accent-color", "#0F766E");

            // A device with an older accent can set its mode without writing that old accent back.
            await ModeButton(page, "Dark").ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-accent-color", "#0F766E");
            await Expect(root).ToHaveAttributeAsync("data-theme", "dark");
            var response = await page.ReloadAsync();
            var html = await response!.TextAsync();
            Assert.Contains("data-appearance-mode=\"dark\"", html);
            Assert.Contains("data-accent-color=\"#0F766E\"", html);
            Assert.Equal(new[] { "dark", "dark", "#0F766E" }, await page.EvaluateAsync<string[]>("() => window.initialTheme"));

            await CheckThemedScreensAsync(page, touch, origin, output);
            await OpenAppearanceAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Restore defaults" }).ClickAsync();
            await Expect(root).ToHaveAttributeAsync("data-accent-color", "#176B9C");
            await Expect(ModeButton(page, "System")).ToHaveAttributeAsync("aria-pressed", "true");

            await menu.ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Log out", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Log out", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(origin + "/login");
            await Expect(AccountMenu(page)).ToHaveCountAsync(0);
            await Expect(root).ToHaveAttributeAsync("data-appearance-mode", "system");
            await LoginForNavigationAsync(page, origin, username, password);
            await Expect(root).ToHaveAttributeAsync("data-accent-color", "#176B9C");
            Assert.Empty(errors);
            Assert.Empty(touchErrors);
        });
    }

    [RoleNavigationBrowserFact]
    public async Task AppearanceIsAvailableWithoutAnAdministrativeRole()
    {
        await WithNavigationBrowserAsync("appearance-personal", async (browser, origin, username, password, user, _) =>
        {
            var edit = (await Users.GetUserForEditAsync(user.Id))!;
            Assert.True(edit.Roles.Remove(AppRoles.Administrator));
            Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
            await using var context = await browser.NewContextAsync(new() { ColorScheme = ColorScheme.Light });
            var page = await context.NewPageAsync();
            await LoginForNavigationAsync(page, origin, username, password);
            await OpenAppearanceAsync(page);
            await ModeButton(page, "Dark").ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            await page.GetByRole(AriaRole.Button, new() { Name = "Orchid", Exact = true }).ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-accent-color", "#C026D3");
        });
    }

    [RoleNavigationBrowserFact]
    public async Task AppearanceAppliesToPdfControlsAndChatWhilePreservingTheirContent()
    {
        var customers = new CustomerService(database, session.Guard);
        var parts = new PartService(database, session.Guard);
        var criteria = new InspectionCriteriaService(database, session.Guard);
        var customerId = await customers.CreateCustomerAsync(new() { Name = "Appearance fixture" });
        var partId = (await parts.CreatePartAsync(new() { CustomerId = customerId, PartNumber = "THEME-FIXTURE" })).Id!.Value;
        var revisionId = (await criteria.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        using var document = new PdfDocument();
        document.AddPage();
        using var bytes = new MemoryStream();
        document.Save(bytes);
        var revision = (await criteria.GetRevisionAsync(partId, revisionId))!;
        Assert.Equal(CriteriaOperationStatus.Succeeded,
            (await criteria.UploadMasterPrintAsync(partId, revisionId, "theme-fixture.pdf", bytes.ToArray(), revision.Version)).Status);

        await WithNavigationBrowserAsync("appearance-content", async (browser, origin, username, password, user, output) =>
        {
            using var senderSession = new AuthorizationTestSession(database);
            await senderSession.SignInAsync(user.Id);
            var notifications = new ChatNotifications(NullLogger<ChatNotifications>.Instance);
            ChatService Chat(AuthorizationTestSession actor) =>
                new(database, actor.Guard, actor.Evaluator, new HttpContextAccessor(), notifications, TimeProvider.System);
            var sender = Chat(senderSession);
            var recipient = Chat(session);
            var conversation = await sender.OpenDirectAsync(rootId);
            await sender.SendAsync(conversation, "Sent sample message");
            await recipient.SendAsync(conversation, "Received sample message");

            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 1440, Height = 940 },
                ColorScheme = ColorScheme.Light
            });
            var page = await context.NewPageAsync();
            var errors = ObserveErrors(page);
            await LoginForNavigationAsync(page, origin, username, password);
            await OpenAppearanceAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Teal", Exact = true }).ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-accent-color", "#0F766E");
            await page.GotoAsync($"{origin}/parts/{partId}/inspection-criteria/{revisionId}");
            await page.GetByRole(AriaRole.Tab, new() { Name = "Master Print", Exact = true }).ClickAsync();
            var frame = page.FrameLocator("iframe[title='Master print PDF']");
            var canvas = frame.Locator("#pdf-page");
            await Expect(canvas).ToBeVisibleAsync();
            var toolbar = frame.Locator(".viewer-toolbar");
            var lightToolbar = await BackgroundAsync(toolbar);
            await Expect(frame.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
            var pdf = await canvas.EvaluateAsync<string>("canvas => canvas.toDataURL()");

            await AccountMenu(page).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Switch to dark mode", Exact = true }).ClickAsync();
            await Expect(frame.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            Assert.NotEqual(lightToolbar, await BackgroundAsync(toolbar));
            Assert.Equal(pdf, await canvas.EvaluateAsync<string>("canvas => canvas.toDataURL()"));
            await page.Keyboard.PressAsync("Escape");
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "pdf-viewer-dark.png") });

            await page.GetByRole(AriaRole.Button, new() { Name = "Open chat", Exact = true }).ClickAsync();
            await page.Locator(".chat-conversation").Filter(new() { HasText = "Test Installation Owner" }).ClickAsync();
            var sent = page.Locator(".chat-message.mine").Filter(new() { HasText = "Sent sample message" });
            var received = page.Locator(".chat-message").Filter(new() { HasText = "Received sample message" });
            await Expect(sent).ToBeVisibleAsync();
            await Expect(received).ToBeVisibleAsync();
            var darkSent = await BackgroundAsync(sent);
            Assert.NotEqual(darkSent, await BackgroundAsync(received));
            await Expect(page.Locator("#chat-message-body")).ToBeEditableAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "chat-messages-dark.png") });

            await AccountMenu(page).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Switch to light mode", Exact = true }).ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
            Assert.NotEqual(darkSent, await BackgroundAsync(sent));
            Assert.NotEqual(await BackgroundAsync(sent), await BackgroundAsync(received));
            await Expect(frame.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
            Assert.Equal(pdf, await canvas.EvaluateAsync<string>("canvas => canvas.toDataURL()"));
            Assert.Empty(errors);
        });
    }

    private static async Task CheckThemedScreensAsync(IPage desktop, IPage tablet, string origin, string output)
    {
        foreach (var page in new[] { desktop, tablet })
        {
            foreach (var (route, selector) in new[]
            {
                ("/production-tracking", ".machine-picker"),
                ("/production-scheduling/settings", ".default-working-days"),
                ("/received-parts", ".app-main .page-heading"),
                ("/inspections", ".inspection-list-page"),
                ("/customers/create", ".customer-form"),
                ("/admin/roles", ".role-list"),
                ("/admin/certification-email-templates", ".email-template-preview")
            })
            {
                await page.GotoAsync(origin + route);
                await Expect(page.Locator(selector).First).ToBeVisibleAsync();
                await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            }
            await page.GotoAsync(origin + "/production-scheduling/settings");
            await Expect(page.Locator(".default-working-days")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"scheduling-dark-{page.ViewportSize!.Width}.png") });

            var launcher = page.GetByRole(AriaRole.Button, new() { Name = "Open chat", Exact = true });
            await launcher.ClickAsync();
            await Expect(page.Locator(".chat-panel")).ToBeVisibleAsync();
            Assert.NotEqual("rgb(255, 255, 255)", await BackgroundAsync(page.Locator(".chat-panel")));
            await page.GetByRole(AriaRole.Button, new() { Name = "Inbox", Exact = true }).ClickAsync();
            await Expect(page.Locator(".inbox")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"chat-dark-{page.ViewportSize!.Width}.png") });
        }
    }

    private static ILocator AccountMenu(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Browser Test User account menu", Exact = true });

    private static ILocator ModeButton(IPage page, string mode) =>
        page.GetByRole(AriaRole.Button, new() { Name = mode, Exact = true });

    private static async Task OpenAppearanceAsync(IPage page)
    {
        await AccountMenu(page).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Appearance", Exact = true }).ClickAsync();
    }

    private static Task<string> BackgroundAsync(ILocator locator) =>
        locator.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");
}
