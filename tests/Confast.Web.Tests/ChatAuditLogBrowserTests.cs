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
    public async Task ChannelAuditLogUsesContextChannelLocalTimeAndModerationPermission()
    {
        await WithNavigationBrowserAsync("chat-audit-log", async (browser, origin, username, password, user, output) =>
        {
            var notifications = new ChatNotifications(NullLogger<ChatNotifications>.Instance);
            var deletedAtUtc = new DateTimeOffset(2026, 7, 15, 0, 30, 45, TimeSpan.Zero);
            var auditClock = new TestClock();
            auditClock.Advance(deletedAtUtc - auditClock.GetUtcNow());
            var chat = new ChatService(database, session.Guard, session.Evaluator, new HttpContextAccessor(), notifications, auditClock);
            var selectedChannel = await chat.CreateChannelAsync("Selected Channel", ChannelVisibility.Private, [user.Id]);
            var sender = await User(AppRoles.QualityId);
            var auditedChannel = await chat.CreateChannelAsync("Audited Channel", ChannelVisibility.Private, [user.Id, sender.Id]);
            using var senderSession = new AuthorizationTestSession(database);
            await senderSession.SignInAsync(sender.Id);
            var senderChat = new ChatService(database, senderSession.Guard, senderSession.Evaluator, new HttpContextAccessor(), notifications, auditClock);
            var message = await senderChat.SendAsync(auditedChannel, "Deleted chat message");
            await chat.DeleteOthersMessageAsync(message, "Audit log browser test reason");
            var liveMessage = await senderChat.SendAsync(auditedChannel, "Message to remove while chat is open");
            var reply = await senderChat.SendAsync(auditedChannel, "Reply that should stay visible", replyToMessageId: liveMessage);

            await using var context = await browser.NewContextAsync(new()
            {
                Locale = "en-US", TimezoneId = "America/Los_Angeles", ViewportSize = new() { Width = 1440, Height = 900 }
            });
            var page = await context.NewPageAsync();
            var errors = ObserveErrors(page);
            await LoginForNavigationAsync(page, origin, username, password);
            var launcher = page.GetByRole(AriaRole.Button, new() { Name = "Open chat", Exact = true });
            var panel = page.GetByRole(AriaRole.Region, new() { Name = "Chat", Exact = true });
            var channels = panel.GetByRole(AriaRole.Button, new() { NameRegex = new("^Channels") });
            var selected = panel.GetByRole(AriaRole.Button, new() { Name = "# Selected Channel", Exact = true });
            var audited = panel.GetByRole(AriaRole.Button, new() { NameRegex = new("^# Audited Channel") });
            var menu = page.GetByRole(AriaRole.Menu, new() { Name = "Channel Menu", Exact = true });
            var auditItem = menu.GetByRole(AriaRole.Menuitem, new() { Name = "Audit Log", Exact = true });
            var log = panel.GetByRole(AriaRole.Dialog, new() { Name = "Audit Log", Exact = true });

            await launcher.ClickAsync();
            await channels.ClickAsync();
            await selected.ClickAsync();
            await Expect(selected).ToHaveClassAsync("chat-conversation selected");
            await Expect(panel.GetByRole(AriaRole.Button, new() { Name = "Moderation History", Exact = true })).ToHaveCountAsync(0);
            await audited.ClickAsync(new() { Button = MouseButton.Right });
            await Expect(menu.Locator(":scope > [role='menuitem']").Last).ToHaveTextAsync("Audit Log");
            await auditItem.ClickAsync();
            await Expect(menu).ToBeHiddenAsync();
            await Expect(log).ToBeVisibleAsync();
            await Expect(log.GetByRole(AriaRole.Heading, new() { Name = "Audit Log", Exact = true })).ToBeVisibleAsync();
            await Expect(log.Locator("#audit-log-description")).ToContainTextAsync("#Audited Channel");
            var headingGap = await log.Locator("#audit-log-description").EvaluateAsync<double>(
                "description => description.getBoundingClientRect().top - description.previousElementSibling.getBoundingClientRect().bottom");
            Assert.InRange(headingGap, 0, 1);
            await Expect(log.GetByRole(AriaRole.Columnheader, new() { Name = "Deleted At", Exact = true })).ToBeVisibleAsync();
            await Expect(log.Locator("time")).ToHaveTextAsync("Jul 14, 2026, 5:30:45 PM");
            await Expect(log.Locator("time")).ToHaveAttributeAsync("datetime", deletedAtUtc.UtcDateTime.ToString("O"));
            await Expect(log.GetByText("Audit log browser test reason")).ToBeVisibleAsync();
            await Expect(log.GetByText("Deleted chat message", new() { Exact = true })).ToHaveCountAsync(0);
            var refresh = log.GetByRole(AriaRole.Button, new() { Name = "Refresh History", Exact = true });
            var close = log.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true });
            var refreshBounds = (await refresh.BoundingBoxAsync())!;
            var closeBounds = (await close.BoundingBoxAsync())!;
            Assert.True(refreshBounds.X + refreshBounds.Width <= closeBounds.X);
            Assert.InRange(Math.Abs(refreshBounds.Y - closeBounds.Y), 0, 1);
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "audit-log.png") });
            await close.PressAsync("Escape");
            await Expect(log).ToBeHiddenAsync();
            await Expect(panel.GetByRole(AriaRole.Button, new() { Name = "Settings for Audited Channel", Exact = true })).ToBeFocusedAsync();
            await Expect(selected).ToHaveClassAsync("chat-conversation selected");

            await audited.ClickAsync();
            var transcript = panel.GetByRole(AriaRole.Log, new() { Name = "Message history", Exact = true });
            var liveRow = transcript.Locator($"[data-chat-message-id='{liveMessage}']");
            var replyRow = transcript.Locator($"[data-chat-message-id='{reply}']");
            await Expect(liveRow).ToBeVisibleAsync();
            await Expect(transcript.Locator($"[data-chat-message-id='{message}']")).ToHaveCountAsync(0);
            await Expect(replyRow.Locator(".chat-reply-preview")).ToBeVisibleAsync();
            await liveRow.HoverAsync();
            await liveRow.GetByRole(AriaRole.Button, new() { Name = "More message actions", Exact = true }).ClickAsync();
            await liveRow.GetByRole(AriaRole.Menuitem, new() { Name = "Delete Message", Exact = true }).ClickAsync();
            var deletion = page.GetByRole(AriaRole.Alertdialog, new() { Name = "Delete Message", Exact = true });
            var reason = deletion.GetByLabel("Moderation Reason");
            var delete = deletion.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true });
            await reason.FillAsync("Removed from the open transcript");
            await Expect(reason).ToHaveCSSAsync("resize", "vertical");
            var fieldBounds = (await reason.BoundingBoxAsync())!;
            var deleteBounds = (await delete.BoundingBoxAsync())!;
            Assert.True(deleteBounds.Y - (fieldBounds.Y + fieldBounds.Height) >= 12);
            await delete.ClickAsync();
            await Expect(liveRow).ToHaveCountAsync(0);
            await Expect(transcript.GetByText("Message deleted", new() { Exact = true })).ToHaveCountAsync(0);
            await Expect(replyRow.GetByText("Reply that should stay visible", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(replyRow.Locator(".chat-reply-preview")).ToHaveCountAsync(0);

            var edit = (await Users.GetUserForEditAsync(user.Id))!;
            Assert.True(edit.Roles.Remove(AppRoles.Administrator));
            edit.Roles.Add(AppRoles.Quality);
            Assert.True((await Users.UpdateUserAsync(edit)).Succeeded);
            await page.ReloadAsync();
            await launcher.ClickAsync();
            await Expect(audited).ToBeVisibleAsync();
            await audited.ClickAsync(new() { Button = MouseButton.Right });
            await Expect(menu).ToBeVisibleAsync();
            await Expect(auditItem).ToHaveCountAsync(0);
            Assert.Empty(errors);
        });
    }
}
