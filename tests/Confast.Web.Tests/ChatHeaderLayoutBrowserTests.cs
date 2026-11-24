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
                var tabs = panel.Locator(".chat-tabs");
                var header = panel.Locator(".chat-thread-header");
                var directRow = panel.Locator(".chat-conversation-row .chat-conversation").Filter(new() { HasText = otherUser.DisplayName });
                var initialHeight = await tabs.EvaluateAsync<double>("tabs => tabs.getBoundingClientRect().height");

                await directRow.ClickAsync();
                await Expect(panel.GetByText("Direct conversation message", new() { Exact = true })).ToBeVisibleAsync();
                await AssertAlignedAsync();

                await tabs.GetByRole(AriaRole.Button, new() { Name = "Channels", Exact = true }).ClickAsync();
                var channelRow = panel.GetByRole(AriaRole.Button, new() { Name = "# Header Layout Check", Exact = true });
                await channelRow.ClickAsync();
                await Expect(header.GetByRole(AriaRole.Button, new() { Name = "Read full channel topic" })).ToBeVisibleAsync();
                await AssertAlignedAsync();

                await tabs.GetByRole(AriaRole.Button, new() { Name = "Conversations", Exact = true }).ClickAsync();
                await Expect(panel.GetByText("Direct conversation message", new() { Exact = true })).ToBeVisibleAsync();
                await AssertAlignedAsync();

                async Task AssertAlignedAsync()
                {
                    var tabBounds = (await tabs.BoundingBoxAsync())!;
                    var headerBounds = (await header.BoundingBoxAsync())!;
                    Assert.Equal(initialHeight, tabBounds.Height, precision: 2);
                    Assert.Equal(tabBounds.Y + tabBounds.Height, headerBounds.Y + headerBounds.Height, precision: 2);
                }
            }
        });
    }
}
