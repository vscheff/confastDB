using Confast.Web.Features.Chat;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    [RoleNavigationBrowserFact]
    public async Task ChatHeaderKeepsItsHeightWhenSwitchingBetweenConversationsAndChannels()
    {
        await WithNavigationBrowserAsync("chat-header-layout", async (browser, origin, username, password, user, _) =>
        {
            var otherUser = await User(AppRoles.QualityId);
            await session.SignInAsync(user.Id);
            var notifications = new ChatNotifications(NullLogger<ChatNotifications>.Instance);
            var chat = new ChatService(database, session.Guard, session.Evaluator, new HttpContextAccessor(), notifications, TimeProvider.System);
            var conversation = await chat.OpenDirectAsync(otherUser.Id);
            await chat.SendAsync(conversation, "Direct conversation message");
            var channel = await chat.CreateChannelAsync("Header Layout Check", ChannelVisibility.Private, [user.Id]);
            await chat.SetChannelTopicAsync(channel, "A channel topic that needs room in the header");

            foreach (var touch in new[] { false, true })
            {
                await using var context = await browser.NewContextAsync(new()
                {
                    HasTouch = touch,
                    ViewportSize = new() { Width = touch ? 1024 : 1440, Height = 900 }
                });
                var page = await context.NewPageAsync();
                await LoginForNavigationAsync(page, origin, username, password);
                await page.GetByRole(AriaRole.Button, new() { Name = "Open chat", Exact = true }).ClickAsync();

                var panel = page.GetByRole(AriaRole.Region, new() { Name = "Chat", Exact = true });
                var tabs = panel.GetByRole(AriaRole.Group, new() { Name = "Chat views" });
                var conversationsTab = tabs.GetByRole(AriaRole.Button, new() { Name = "Conversations", Exact = true });
                var channelsTab = tabs.GetByRole(AriaRole.Button, new() { Name = "Channels", Exact = true });
                var header = panel.Locator(".chat-thread-header");
                var directRow = panel.Locator(".chat-conversation-row .chat-conversation").Filter(new() { HasText = otherUser.DisplayName });
                await Expect(conversationsTab).ToHaveAttributeAsync("aria-pressed", "true");
                await Expect(channelsTab).ToHaveAttributeAsync("aria-pressed", "false");
                var initialTabsHeight = (await tabs.BoundingBoxAsync())!.Height;
                Assert.InRange(initialTabsHeight, touch ? 50 : 36, touch ? 52 : 38);

                await directRow.ClickAsync();
                await Expect(panel.GetByText("Direct conversation message", new() { Exact = true })).ToBeVisibleAsync();
                var initialHeaderHeight = (await header.BoundingBoxAsync())!.Height;

                await conversationsTab.FocusAsync();
                await page.Keyboard.PressAsync("Tab");
                await Expect(channelsTab).ToBeFocusedAsync();
                await Expect(channelsTab).ToHaveCSSAsync("outline-width", "2px");
                await page.Keyboard.PressAsync("Space");
                await Expect(conversationsTab).ToHaveAttributeAsync("aria-pressed", "false");
                await Expect(channelsTab).ToHaveAttributeAsync("aria-pressed", "true");
                var channelRow = panel.GetByRole(AriaRole.Button, new() { Name = "# Header Layout Check", Exact = true });
                await channelRow.ClickAsync();
                await Expect(header.GetByRole(AriaRole.Button, new() { Name = "Read full channel topic" })).ToBeVisibleAsync();
                await AssertStableHeightsAsync();

                await channelsTab.FocusAsync();
                await page.Keyboard.PressAsync("Shift+Tab");
                await Expect(conversationsTab).ToBeFocusedAsync();
                await page.Keyboard.PressAsync("Enter");
                await Expect(conversationsTab).ToHaveAttributeAsync("aria-pressed", "true");
                await Expect(panel.GetByText("Direct conversation message", new() { Exact = true })).ToBeVisibleAsync();
                await AssertStableHeightsAsync();

                async Task AssertStableHeightsAsync()
                {
                    Assert.Equal(initialTabsHeight, (await tabs.BoundingBoxAsync())!.Height, precision: 2);
                    Assert.Equal(initialHeaderHeight, (await header.BoundingBoxAsync())!.Height, precision: 2);
                }
            }
        });
    }
}
