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
    public async Task ChatHighlightsRepliesAndApplicableMentionsInChannelAndThreadViews()
    {
        await WithNavigationBrowserAsync("chat-highlights", async (browser, origin, username, password, user, _) =>
        {
            var notifications = new ChatNotifications(NullLogger<ChatNotifications>.Instance);
            await session.SignInAsync(user.Id);
            var chat = new ChatService(database, session.Guard, session.Evaluator, new HttpContextAccessor(), notifications, TimeProvider.System);
            var sender = await User(AppRoles.QualityId);
            using var senderSession = new AuthorizationTestSession(database);
            await senderSession.SignInAsync(sender.Id);
            var senderChat = new ChatService(database, senderSession.Guard, senderSession.Evaluator, new HttpContextAccessor(), notifications, TimeProvider.System);
            var channel = await chat.CreateChannelAsync("Highlight Check", ChannelVisibility.Private, [user.Id, sender.Id]);

            var ownEveryone = await chat.SendAsync(channel, "My update for @everyone");
            var ownHere = await chat.SendAsync(channel, "My update for @here");
            var ownSelf = await chat.SendAsync(channel, $"A reminder for @{user.DisplayName}");
            var ownOther = await chat.SendAsync(channel, $"A reminder for @{sender.DisplayName}");
            var ownReply = await chat.SendAsync(channel, "Adding to my update", replyToMessageId: ownEveryone);
            var plain = await senderChat.SendAsync(channel, "A routine update");
            var reply = await senderChat.SendAsync(channel, "Reply to your update", replyToMessageId: ownEveryone);
            var direct = await senderChat.SendAsync(channel, $"An update for @{user.DisplayName}");
            var everyone = await senderChat.SendAsync(channel, "An update for @everyone");
            var offlineHere = await senderChat.SendAsync(channel, "An update for @here before login");
            var threadId = await chat.CreateChannelThreadAsync(channel, "Highlight Thread", "A thread update");
            var ownThread = await chat.SendAsync(channel, "My thread update", channelThreadId: threadId);
            var ownThreadMention = await chat.SendAsync(channel, "My thread update for @here", channelThreadId: threadId);
            var plainThread = await senderChat.SendAsync(channel, "A routine thread update", channelThreadId: threadId);
            var threadMention = await senderChat.SendAsync(channel, "Thread update for @everyone", channelThreadId: threadId);

            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
            var page = await context.NewPageAsync();
            var errors = ObserveErrors(page);
            await LoginForNavigationAsync(page, origin, username, password);
            var panel = page.GetByRole(AriaRole.Region, new() { Name = "Chat", Exact = true });
            await page.GetByRole(AriaRole.Button, new() { Name = "Open chat", Exact = true }).ClickAsync();
            await panel.GetByRole(AriaRole.Button, new() { NameRegex = new("^Channels") }).ClickAsync();
            await panel.GetByRole(AriaRole.Button, new() { NameRegex = new("^# Highlight Check") }).ClickAsync();

            var transcript = panel.GetByRole(AriaRole.Log, new() { Name = "Message history", Exact = true });
            var replyRow = Message(transcript, reply);
            await Expect(replyRow).ToBeVisibleAsync();
            var highlight = await replyRow.EvaluateAsync<string>("row => getComputedStyle(row).backgroundColor");
            var border = await replyRow.EvaluateAsync<string>("row => getComputedStyle(row).boxShadow");
            Assert.Contains("inset", border);
            var plainBackground = await Message(transcript, plain).EvaluateAsync<string>("row => getComputedStyle(row).backgroundColor");
            Assert.NotEqual(plainBackground, highlight);
            foreach (var id in new[] { ownOther, ownReply, offlineHere })
            {
                await Expect(Message(transcript, id)).ToHaveCSSAsync("background-color", plainBackground);
                await Expect(Message(transcript, id)).ToHaveCSSAsync("box-shadow", "none");
            }

            foreach (var id in new[] { ownEveryone, ownHere, ownSelf, direct, everyone })
                await ExpectHighlight(Message(transcript, id));
            await Message(transcript, direct).HoverAsync();
            await ExpectHighlight(Message(transcript, direct));

            await transcript.Locator(".chat-thread-notice-title").Filter(new() { HasText = "Highlight Thread" }).ClickAsync();
            var sidebar = panel.GetByRole(AriaRole.Log, new() { Name = "Thread messages", Exact = true });
            var plainThreadBackground = await Message(sidebar, plainThread).EvaluateAsync<string>("row => getComputedStyle(row).backgroundColor");
            await Expect(Message(sidebar, ownThread)).ToHaveCSSAsync("background-color", plainThreadBackground);
            await Expect(Message(sidebar, ownThread)).ToHaveCSSAsync("box-shadow", "none");
            await ExpectHighlight(Message(sidebar, ownThreadMention));
            await ExpectHighlight(Message(sidebar, threadMention));
            await panel.GetByTitle("Open thread in full view").ClickAsync();
            await page.Mouse.MoveAsync(0, 0);
            await Expect(Message(transcript, ownThread)).ToHaveCSSAsync("background-color", plainBackground);
            await ExpectHighlight(Message(transcript, ownThreadMention));
            await ExpectHighlight(Message(transcript, threadMention));
            Assert.Empty(errors);

            async Task ExpectHighlight(ILocator row)
            {
                await Expect(row).ToHaveCSSAsync("background-color", highlight);
                await Expect(row).ToHaveCSSAsync("box-shadow", border);
            }
        });

        static ILocator Message(ILocator transcript, long id) => transcript.Locator($"[data-chat-message-id='{id}']");
    }
}
