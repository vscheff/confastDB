using Confast.Web.Features.Authorization;
using Confast.Web.Features.Chat;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChatAuthorizationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly ChatNotifications notifications = new(NullLogger<ChatNotifications>.Instance);
    private AuthorizationTestSession actor = null!, sender = null!, outsider = null!, root = null!;
    private string actorId = "", senderId = "", outsiderId = "";
    private long channel, direct, group, category, threadId, ownMessage, otherMessage, pollMessage;
    private long answerId;
    private static readonly string[] ChatKeys = PermissionCatalog.All.Where(x => x.Key.StartsWith("Chat.")).Select(x => x.Key).ToArray();
    private ChatService Chat(AuthorizationTestSession session, EffectivePermissionService? evaluator = null, TimeProvider? clock = null) =>
        new(database, new(evaluator ?? session.Evaluator, new HttpContextAccessor()), evaluator ?? session.Evaluator,
            new HttpContextAccessor(), notifications, clock ?? TimeProvider.System);

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        root = new(database); await root.SignInAsync(await root.ProvisionRootAsync());
        await using var db = database.CreateDbContext();
        var users = Enumerable.Range(0, 3).Select(i => new ApplicationUser { UserName = $"chat.security.{i}",
            DisplayName = $"Chat Test {i}", SecurityStamp = Guid.NewGuid().ToString(), IsActive = true }).ToArray();
        db.Users.AddRange(users); await db.SaveChangesAsync();
        actorId = users[0].Id; senderId = users[1].Id; outsiderId = users[2].Id;
        actor = new(database); sender = new(database); outsider = new(database);
        await actor.SignInAsync(actorId); await sender.SignInAsync(senderId); await outsider.SignInAsync(outsiderId);
        await Grants(actorId, ChatKeys); await Grants(senderId, ChatKeys); await Grants(outsiderId, ChatKeys);
        var chat = Chat(actor);
        channel = await chat.CreateChannelAsync("Private security", ChannelVisibility.Private, [senderId]);
        direct = await chat.OpenDirectAsync(senderId);
        group = await chat.CreateGroupAsync([senderId, outsiderId]);
        category = await chat.CreateChannelGroupAsync("Security folder", channel);
        threadId = await chat.CreateChannelThreadAsync(channel, "Security thread", "Initial");
        ownMessage = await chat.SendAsync(channel, "Actor's message");
        otherMessage = await Chat(sender).SendAsync(channel, "Other sender's message", attachmentFileName: "private.txt", attachmentContent: [65, 66]);
        pollMessage = await chat.CreatePollAsync(channel, "Question", [new("Yes"), new("No")]);
        answerId = await db.Set<ChatPollAnswer>().Where(x => x.PollMessageId == pollMessage).Select(x => x.Id).FirstAsync();
    }
    public Task DisposeAsync() { actor.Dispose(); sender.Dispose(); outsider.Dispose(); root.Dispose(); return Task.CompletedTask; }

    private async Task Grants(string userId, params string[] keys)
    {
        await using var db = database.CreateDbContext();
        var role = await db.Roles.SingleOrDefaultAsync(x => x.Name == "chat-" + userId);
        if (role is null)
        {
            role = new ApplicationRole { Name = "chat-" + userId, NormalizedName = "CHAT-" + userId.ToUpperInvariant() };
            db.Roles.Add(role); await db.SaveChangesAsync();
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = role.Id });
            await db.SaveChangesAsync();
        }
        await db.RolePermissions.Where(x => x.RoleId == role.Id).ExecuteDeleteAsync();
        db.RolePermissions.AddRange(keys.Distinct().Select(key => new RolePermission { RoleId = role.Id, PermissionKey = key }));
        await db.SaveChangesAsync();
    }

    public static IEnumerable<object[]> PermissionCases() => ChatKeys.Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(PermissionCases))]
    public async Task EachPermission_IsRequiredAtTheService_AndWorksWithOnlyItsPrerequisites(string key)
    {
        await Grants(actorId, ChatKeys.Except([key]).ToArray());
        var exception = await Record.ExceptionAsync(() => Invoke(key, Chat(actor)));
        Assert.True(exception is AuthorizationDeniedException or UnauthorizedAccessException, $"{key}: {exception}");
        var required = PermissionCatalog.ExpandRequirements([key]).ToArray();
        await Grants(actorId, required);
        if (key == Permissions.Chat.AdministerChannels)
        {
            // This grant is an ownership alternative, never independent update authority.
            await Grants(actorId, PermissionCatalog.ExpandRequirements([key, Permissions.Chat.UpdateChannels]).ToArray());
        }
        await Invoke(key, Chat(actor));
    }

    private async Task Invoke(string key, ChatService chat)
    {
        switch (key)
        {
            case Permissions.Chat.Access: await chat.GetThreadAsync(channel); break;
            case Permissions.Chat.CreateConversations: await chat.OpenDirectAsync(outsiderId); break;
            case Permissions.Chat.SendMessages: await chat.SendAsync(channel, "Authorized"); break;
            case Permissions.Chat.ScheduleMessages: await chat.SendWithAttachmentsAsync(channel, "Later", [], scheduledAtUtc: DateTime.UtcNow.AddHours(1)); break;
            case Permissions.Chat.CreateThreads: await chat.CreateChannelThreadAsync(channel, "Another", "Initial"); break;
            case Permissions.Chat.CreatePolls: await chat.CreatePollAsync(channel, "Another?", [new("Yes"), new("No")]); break;
            case Permissions.Chat.VotePolls: await chat.TogglePollVoteAsync(pollMessage, answerId); break;
            case Permissions.Chat.React: await chat.ToggleReactionAsync(otherMessage, "👍"); break;
            case Permissions.Chat.PinMessages: await chat.SetMessagePinnedAsync(otherMessage, true); break;
            case Permissions.Chat.EditOwnMessages: await chat.EditOwnMessageAsync(ownMessage, "Edited"); break;
            case Permissions.Chat.DeleteOwnMessages: await chat.DeleteOwnMessageAsync(ownMessage); break;
            case Permissions.Chat.DeleteOthersMessages: await chat.DeleteOthersMessageAsync(otherMessage, "Channel rules"); break;
            case Permissions.Chat.CreateChannels: await chat.DuplicateChannelAsync(channel, "Copy"); break;
            case Permissions.Chat.UpdateChannels: await chat.RenameChannelAsync(channel, "Updated"); break;
            case Permissions.Chat.DeleteChannels: await chat.DeleteChannelAsync(channel); break;
            case Permissions.Chat.ManagePrivateChannelMembers: await chat.AddPrivateMemberAsync(channel, outsiderId); break;
            case Permissions.Chat.CreateCategories: await chat.CreateChannelGroupAsync("New folder"); break;
            case Permissions.Chat.ManageCategories: await chat.RenameChannelGroupAsync(category, "Renamed"); break;
            case Permissions.Chat.ReorderChannels: await chat.MoveChannelAsync(channel, null, null); break;
            case Permissions.Chat.ManageGroupConversations: await chat.UpdateGroupAsync(group, "Renamed group"); break;
            case Permissions.Chat.AdministerChannels:
                // Transfer ownership away in persisted state without changing membership.
                await using (var db = database.CreateDbContext())
                    await db.ChatConversationMembers.Where(x => x.ConversationId == channel && x.UserId == actorId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsOwner, false));
                await chat.SetChannelTopicAsync(channel, "Administrative edit"); break;
            default: throw new InvalidOperationException(key);
        }
    }

    [Fact]
    public async Task AccessRevocation_DeniesContentAndActions_PreservesData_AndRegrantRestoresMembershipOnly()
    {
        await Grants(actorId, ChatKeys.Except([Permissions.Chat.Access]).ToArray());
        var chat = Chat(actor);
        Func<Task>[] reads = [async () => await chat.GetConversationsAsync(), async () => await chat.GetThreadAsync(channel),
            async () => await chat.GetInboxMessagesAsync(), async () => await chat.GetScheduledMessagesAsync(),
            async () => await chat.GetNotificationUnreadSummaryAsync(), async () => await chat.GetChannelGroupsAsync(),
            async () => await chat.GetChannelThreadsAsync(channel), async () => await chat.GetIncomingMessagePreviewAsync(otherMessage),
            async () => await chat.GetModerationHistoryAsync(channel), async () => await chat.SendAsync(channel, "denied")];
        foreach (var read in reads) await Assert.ThrowsAsync<AuthorizationDeniedException>(read);
        await using var db = database.CreateDbContext();
        var attachment = await db.ChatAttachments.Select(x => x.Id).SingleAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => chat.GetAttachmentAsync(attachment));
        Assert.True(await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == channel && x.UserId == actorId));
        Assert.Equal("Actor's message", (await db.ChatMessages.FindAsync(ownMessage))!.Body);
        await Grants(actorId, Permissions.Chat.Access);
        Assert.NotEmpty((await chat.GetThreadAsync(channel)).Messages);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => chat.SendAsync(channel, "denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat(root).GetThreadAsync(channel));
        Assert.Null(await Chat(outsider).GetAttachmentAsync(attachment));
    }

    [Fact]
    public async Task Moderation_IsAtomicConcurrentAppendOnly_AndAttachmentsDisappear()
    {
        await Chat(actor).SetMessagePinnedAsync(otherMessage, true);
        await Chat(actor).AddPrivateMemberAsync(channel, outsiderId);
        var first = Chat(actor); var second = Chat(outsider);
        var results = await Task.WhenAll(new[] { first, second }.Select(async c => await Record.ExceptionAsync(() => c.DeleteOthersMessageAsync(otherMessage, "Duplicate spam"))));
        Assert.Single(results, x => x is null);
        Assert.Single(results, x => x is InvalidOperationException);
        var history = Assert.Single(await first.GetModerationHistoryAsync(channel));
        Assert.Equal(senderId, history.OriginalSenderUserId); Assert.Contains(history.ModeratorUserId, new[] { actorId, outsiderId }); Assert.True(history.WasPinned);
        Assert.NotEqual(Guid.Empty, history.OperationId);
        var message = (await first.GetThreadAsync(channel)).Messages.Single(x => x.Id == otherMessage);
        Assert.True(message.IsDeleted); Assert.Null(message.Body); Assert.Empty(message.Attachments); Assert.Null(message.PinnedAtUtc);
        await using var db = database.CreateDbContext();
        var stored = (await db.ChatMessages.FindAsync(otherMessage))!;
        Assert.Equal(senderId, stored.SenderUserId); Assert.Equal(history.ModeratorUserId, stored.DeletedByUserId);
        Assert.Equal("Other sender's message", stored.Body);
        var attachmentId = await db.ChatAttachments.Select(x => x.Id).SingleAsync();
        Assert.Null(await first.GetAttachmentAsync(attachmentId));
        foreach (var sql in new[] { "UPDATE chat_moderation_history SET reason = 'changed'", "DELETE FROM chat_moderation_history", "TRUNCATE chat_moderation_history" })
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Single(await first.GetModerationHistoryAsync(channel));
    }

    [Theory]
    [InlineData("own")][InlineData("poll")][InlineData("direct")][InlineData("group")][InlineData("gif")][InlineData("empty")][InlineData("outsider")][InlineData("root")]
    public async Task Moderation_RejectsInvalidScopeAndReason(string mode)
    {
        var id = mode switch
        {
            "own" => ownMessage, "poll" => pollMessage,
            "direct" => await Chat(sender).SendAsync(direct, "Private DM"),
            "group" => await Chat(sender).SendAsync(group, "Group chat"),
            "gif" => await Chat(sender).SendGifAsync(channel, "abc123"), _ => otherMessage
        };
        var service = Chat(mode == "root" ? root : mode == "outsider" ? outsider : actor);
        Assert.NotNull(await Record.ExceptionAsync(() => service.DeleteOthersMessageAsync(id, mode == "empty" ? " " : "Reason")));
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.Set<ChatModerationHistory>().ToArrayAsync());
        Assert.Null((await db.ChatMessages.FindAsync(id))!.DeletedAtUtc);
    }

    [Fact]
    public async Task ComposedReads_UseOneSnapshot_AndNewOperationsObserveRevocation()
    {
        var evaluator = actor.Evaluator; var chat = Chat(actor, evaluator);
        var before = evaluator.SnapshotCount;
        await chat.GetThreadAsync(channel, channelThreadId: threadId);
        Assert.Equal(before + 1, evaluator.SnapshotCount);
        before = evaluator.SnapshotCount;
        await chat.StartGroupAsync([senderId, outsiderId]);
        Assert.Equal(before + 1, evaluator.SnapshotCount);
        await Grants(actorId, Permissions.Chat.Access);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => chat.SendAsync(channel, "Revoked"));
        Assert.True(evaluator.SnapshotCount > before + 1);
    }

    [Fact]
    public async Task EveryPublicProtectedBoundary_RequiresAccess_EvenWithEveryActionGrant()
    {
        await using var db = database.CreateDbContext();
        var attachment = await db.ChatAttachments.Select(x => x.Id).SingleAsync();
        await Grants(actorId, ChatKeys.Except([Permissions.Chat.Access]).ToArray());
        var c = Chat(actor);
        (string Name, Func<Task> Call)[] calls =
        [
            (nameof(c.GetCurrentUserIdAsync), async () => await c.GetCurrentUserIdAsync()),
            (nameof(c.CanAdministerChannelsAsync), async () => await c.CanAdministerChannelsAsync()),
            (nameof(c.GetLastReactionEmojiAsync), async () => await c.GetLastReactionEmojiAsync()),
            (nameof(c.GetEmojiTonePreferencesAsync), async () => await c.GetEmojiTonePreferencesAsync()),
            (nameof(c.SetEmojiTonePreferenceAsync), () => c.SetEmojiTonePreferenceAsync("👍", "👍🏻")),
            (nameof(c.GetActiveUsersAsync), async () => await c.GetActiveUsersAsync()),
            (nameof(c.OpenDirectAsync), async () => await c.OpenDirectAsync(senderId)),
            (nameof(c.CreateChannelAsync), async () => await c.CreateChannelAsync("Denied", ChannelVisibility.Private, [])),
            (nameof(c.GetConversationsAsync), async () => await c.GetConversationsAsync()),
            (nameof(c.GetChannelGroupsAsync), async () => await c.GetChannelGroupsAsync()),
            (nameof(c.CreateChannelGroupAsync), async () => await c.CreateChannelGroupAsync("Denied")),
            (nameof(c.RenameChannelGroupAsync), () => c.RenameChannelGroupAsync(category, "Denied")),
            (nameof(c.DeleteChannelGroupAsync), () => c.DeleteChannelGroupAsync(category)),
            (nameof(c.MoveChannelAsync), () => c.MoveChannelAsync(channel, null, null)),
            (nameof(c.MoveChannelGroupAsync), () => c.MoveChannelGroupAsync(category, null)),
            (nameof(c.MoveLayoutItemAsync), () => c.MoveLayoutItemAsync(false, channel, null, null, null)),
            (nameof(c.GetTotalUnreadCountAsync), async () => await c.GetTotalUnreadCountAsync()),
            (nameof(c.HasUnreadMentionAsync), async () => await c.HasUnreadMentionAsync()),
            (nameof(c.HasUnreadAlertAsync), async () => await c.HasUnreadAlertAsync()),
            (nameof(c.AddPrivateMemberAsync), () => c.AddPrivateMemberAsync(channel, outsiderId)),
            (nameof(c.RemovePrivateMemberAsync), () => c.RemovePrivateMemberAsync(channel, senderId)),
            (nameof(c.SetChannelTopicAsync), () => c.SetChannelTopicAsync(channel, "Denied")),
            (nameof(c.RenameChannelAsync), () => c.RenameChannelAsync(channel, "Denied")),
            (nameof(c.GetChannelMembersAsync), async () => await c.GetChannelMembersAsync(channel)),
            (nameof(c.CreateChannelThreadAsync), async () => await c.CreateChannelThreadAsync(channel, "Denied", "Denied")),
            (nameof(c.GetChannelThreadsAsync), async () => await c.GetChannelThreadsAsync(channel)),
            (nameof(c.GetActiveChannelThreadsAsync), async () => await c.GetActiveChannelThreadsAsync()),
            (nameof(c.GetThreadAsync), async () => await c.GetThreadAsync(channel)),
            (nameof(c.SendAsync), async () => await c.SendAsync(channel, "Denied")),
            (nameof(c.SendWithAttachmentsAsync), async () => await c.SendWithAttachmentsAsync(channel, "Denied", [])),
            (nameof(c.GetAttachmentAsync), async () => await c.GetAttachmentAsync(attachment)),
            (nameof(c.AddReactionAsync), () => c.AddReactionAsync(otherMessage, "👍")),
            (nameof(c.ToggleReactionAsync), () => c.ToggleReactionAsync(otherMessage, "👍")),
            (nameof(c.MarkReadAsync), () => c.MarkReadAsync(channel, otherMessage)),
            (nameof(c.MarkUnreadAsync), () => c.MarkUnreadAsync(channel, otherMessage)),
            (nameof(c.MarkConversationReadAsync), async () => await c.MarkConversationReadAsync(channel)),
            (nameof(c.DeleteOwnMessageAsync), () => c.DeleteOwnMessageAsync(ownMessage)),
            (nameof(c.EditOwnMessageAsync), () => c.EditOwnMessageAsync(ownMessage, "Denied")),
            (nameof(c.SetMessagePinnedAsync), () => c.SetMessagePinnedAsync(otherMessage, true)),
            (nameof(c.SetChannelPinnedToTopAsync), () => c.SetChannelPinnedToTopAsync(channel, true)),
            (nameof(c.DuplicateChannelAsync), async () => await c.DuplicateChannelAsync(channel, "Denied")),
            (nameof(c.MoveChannelToCategoryEdgeAsync), () => c.MoveChannelToCategoryEdgeAsync(channel, true)),
            (nameof(c.DeleteChannelAsync), () => c.DeleteChannelAsync(channel)),
            (nameof(c.HideDirectConversationAsync), () => c.HideDirectConversationAsync(direct)),
            (nameof(c.SetConversationPinnedToTopAsync), () => c.SetConversationPinnedToTopAsync(direct, true)),
            (nameof(c.SetConversationMutedAsync), () => c.SetConversationMutedAsync(group, true)),
            (nameof(c.AddConversationMembersAsync), async () => await c.AddConversationMembersAsync(group, [senderId])),
            (nameof(c.CreateGroupAsync), async () => await c.CreateGroupAsync([senderId, outsiderId])),
            (nameof(c.StartGroupAsync), async () => await c.StartGroupAsync([senderId, outsiderId])),
            (nameof(c.GetGroupIconAsync), async () => await c.GetGroupIconAsync(group)),
            (nameof(c.RenameGroupAsync), () => c.RenameGroupAsync(group, "Denied")),
            (nameof(c.UpdateGroupAsync), () => c.UpdateGroupAsync(group, "Denied")),
            (nameof(c.CreatePollAsync), async () => await c.CreatePollAsync(channel, "Denied?", [new("Yes"), new("No")])),
            (nameof(c.TogglePollVoteAsync), () => c.TogglePollVoteAsync(pollMessage, answerId)),
            (nameof(c.GetGifFavoritesAsync), async () => await c.GetGifFavoritesAsync()),
            (nameof(c.SetGifFavoriteAsync), () => c.SetGifFavoriteAsync("abc123", true)),
            (nameof(c.SendGifAsync), async () => await c.SendGifAsync(channel, "abc123")),
            (nameof(c.RecordGifRequestAsync), () => c.RecordGifRequestAsync()),
            (nameof(c.ResolveGifSearchAsync), async () => await c.ResolveGifSearchAsync("test", 0, 1, () => Task.FromResult(new ChatGifResult([], 0, null)))),
            (nameof(c.ResolveGifsAsync), async () => await c.ResolveGifsAsync([], _ => Task.FromResult(new ChatGifResult([], 0, null)))),
            (nameof(c.GetNotificationSettingsAsync), async () => await c.GetNotificationSettingsAsync()),
            (nameof(c.SetChannelNotificationModeAsync), () => c.SetChannelNotificationModeAsync(channel, ChatNotificationMode.AllMessages)),
            (nameof(c.SetChannelMutedAsync), () => c.SetChannelMutedAsync(channel, true)),
            (nameof(c.SetCategoryNotificationModeAsync), () => c.SetCategoryNotificationModeAsync(category, ChatNotificationMode.AllMessages)),
            (nameof(c.SetCategoryMutedAsync), () => c.SetCategoryMutedAsync(category, true)),
            (nameof(c.GetNotificationUnreadCountsAsync), async () => await c.GetNotificationUnreadCountsAsync()),
            (nameof(c.GetNotificationUnreadSummaryAsync), async () => await c.GetNotificationUnreadSummaryAsync()),
            (nameof(c.GetIncomingMessagePreviewAsync), async () => await c.GetIncomingMessagePreviewAsync(otherMessage)),
            (nameof(c.GetInboxMessagesAsync), async () => await c.GetInboxMessagesAsync()),
            (nameof(c.MarkMessageReadAsync), () => c.MarkMessageReadAsync(channel, otherMessage)),
            (nameof(c.MarkAllUnreadReadAsync), () => c.MarkAllUnreadReadAsync()),
            (nameof(c.GetScheduledMessagesAsync), async () => await c.GetScheduledMessagesAsync()),
            (nameof(c.CancelScheduledMessageAsync), async () => await c.CancelScheduledMessageAsync(1)),
            (nameof(c.DeleteOthersMessageAsync), () => c.DeleteOthersMessageAsync(otherMessage, "Denied")),
            (nameof(c.GetModerationHistoryAsync), async () => await c.GetModerationHistoryAsync(channel))
        ];
        var actual = typeof(ChatService).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly).Where(x => typeof(Task).IsAssignableFrom(x.ReturnType)).Select(x => x.Name)
            .Except([nameof(c.LeaveGroupAsync)]).Order().ToArray();
        Assert.Equal(actual, calls.Select(x => x.Name).Order().ToArray());
        foreach (var call in calls)
        {
            var failure = await Record.ExceptionAsync(call.Call);
            Assert.True(failure is AuthorizationDeniedException, $"{call.Name}: {failure}");
        }
        // Narrow self-scoped exit remains possible without returning protected content.
        await c.LeaveGroupAsync(group);
        Assert.False(await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == group && x.UserId == actorId));
    }

    [Fact]
    public async Task CategoryAndOwnerOverrides_CannotSubstituteForOtherActionsOrPrivateMembership()
    {
        await Grants(actorId, Permissions.Chat.Access, Permissions.Chat.CreateCategories);
        await Chat(actor).CreateChannelGroupAsync("Creation only");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).RenameChannelGroupAsync(category, "Denied"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).CreateChannelGroupAsync("Mixed denied", channel));
        await Grants(actorId, Permissions.Chat.Access, Permissions.Chat.ManageCategories);
        await Chat(actor).RenameChannelGroupAsync(category, "Managed");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).CreateChannelGroupAsync("Denied"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).MoveChannelAsync(channel, null, null));
        await Grants(actorId, Permissions.Chat.Access, Permissions.Chat.ReorderChannels);
        await Chat(actor).MoveChannelAsync(channel, null, null);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).DeleteChannelGroupAsync(category));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat(root).MoveChannelAsync(channel, null, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat(root).AddPrivateMemberAsync(channel, root.ActorId));
        await Chat(root).SetChannelTopicAsync(channel, "Narrow administrative metadata override");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat(root).GetThreadAsync(channel));
    }

    [Fact]
    public async Task Moderation_WorksInChannelThreadsAndPublicChannels_AndRevocationBeforeConfirmationDenies()
    {
        var threaded = await Chat(sender).SendAsync(channel, "Thread message", channelThreadId: threadId);
        await Chat(actor).DeleteOthersMessageAsync(threaded, "Thread rules");
        Assert.Equal(threadId, Assert.Single(await Chat(actor).GetModerationHistoryAsync(channel)).ChannelThreadId);
        var publicId = await Chat(sender).CreateChannelAsync("Public moderation", ChannelVisibility.Public, []);
        var message = await Chat(sender).SendAsync(publicId, "Public message");
        await Chat(actor).DeleteOthersMessageAsync(message, "Public rules");
        Assert.True((await Chat(actor).GetThreadAsync(publicId)).Messages.Single(x => x.Id == message).IsDeleted);
        await Grants(actorId, Permissions.Chat.Access);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).DeleteOthersMessageAsync(otherMessage, "Stale confirmation"));
        await using var db = database.CreateDbContext();
        Assert.Null((await db.ChatMessages.FindAsync(otherMessage))!.DeletedAtUtc);
    }

    [Fact]
    public async Task SubscriptionHints_RequireFreshContentAccess_AndRemovedMembersLoseContent()
    {
        var hints = new List<long?>();
        using var subscription = notifications.Subscribe(actorId, id => hints.Add(id));
        await Grants(actorId, ChatKeys.Except([Permissions.Chat.Access]).ToArray());
        var message = await Chat(sender).SendAsync(channel, "Sensitive new content");
        Assert.Contains(message, hints); // The transport carries an ID, never message text.
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).GetIncomingMessagePreviewAsync(message));
        await Grants(actorId, ChatKeys);
        await Chat(sender).SendWithAttachmentsAsync(channel, "Actor's private draft", [], scheduledAtUtc: DateTime.UtcNow.AddHours(1));
        await using var db = database.CreateDbContext();
        await db.ChatConversationMembers.Where(x => x.UserId == actorId && x.ConversationId == channel).ExecuteDeleteAsync();
        hints.Clear();
        var later = await Chat(sender).SendAsync(channel, "After membership removal");
        Assert.DoesNotContain(later, hints);
        Assert.Null(await Chat(actor).GetIncomingMessagePreviewAsync(later));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat(actor).GetThreadAsync(channel));
        Assert.DoesNotContain(await Chat(actor).GetInboxMessagesAsync(), x => x.ConversationId == channel);
    }

    [Fact]
    public async Task ScheduledDelivery_RechecksRevokedAccess_WithoutPrivilegedFallback()
    {
        await Chat(actor).SendWithAttachmentsAsync(channel, "Should not deliver", [], scheduledAtUtc: DateTime.UtcNow.AddHours(1));
        await using var db = database.CreateDbContext();
        await Grants(actorId, ChatKeys.Except([Permissions.Chat.Access]).ToArray());
        Assert.Equal(0, await Chat(actor, clock: new DeliveryTime()).DeliverDueMessagesAsync());
        Assert.NotNull((await db.Set<ChatScheduledMessage>().SingleAsync()).Failure);
        Assert.False(await db.ChatMessages.AnyAsync(x => x.Body == "Should not deliver"));
    }

    private sealed class DeliveryTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddHours(2);
    }

    [Fact]
    public async Task PublicEnrollment_RunsOnlyAfterAccess_AndDoesNotGrantPrivateMembership()
    {
        var publicId = await Chat(sender).CreateChannelAsync("Organization wide", ChannelVisibility.Public, []);
        await using var db = database.CreateDbContext();
        await db.ChatConversationMembers.Where(x => x.ConversationId == publicId && x.UserId == actorId).ExecuteDeleteAsync();
        await Grants(actorId, Permissions.Chat.SendMessages);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).GetConversationsAsync());
        Assert.False(await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == publicId && x.UserId == actorId));
        await Grants(actorId, Permissions.Chat.Access);
        Assert.Contains(await Chat(actor).GetConversationsAsync(), x => x.Id == publicId);
        Assert.DoesNotContain(await Chat(root).GetConversationsAsync(), x => x.Id == channel);
    }

    [Fact]
    public async Task Presence_RequiresAccess_ExceptEndingTheActorsOwnSession()
    {
        var presence = new UserPresenceService(database, new FixedUser(actorId), actor.Guard, notifications, TimeProvider.System);
        var sessionId = Guid.NewGuid();
        await presence.HeartbeatAsync(sessionId, 0);
        await Grants(actorId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => presence.GetVisibleAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => presence.GetCurrentAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => presence.HeartbeatAsync(sessionId, 0));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => presence.SetPreferenceAsync(UserPresencePreference.Invisible));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => presence.SetCustomStatusAsync("👍", "Denied"));
        await presence.EndSessionAsync(sessionId);
        await using var db = database.CreateDbContext();
        Assert.False(await db.UserPresenceSessions.AnyAsync(x => x.Id == sessionId));
    }

    [Fact]
    public async Task Avatars_RequireAccessForReads_ButSelfProfileUpdatesRemainScoped()
    {
        await Grants(actorId);
        var pictures = new UserProfilePictureService(database, actor.Guard, new HttpContextAccessor(), notifications);
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 };
        await pictures.SaveAsync(png);
        Assert.Equal("Chat Test 0", await pictures.GetCurrentUserDisplayNameAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pictures.GetAsync(actorId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pictures.GetVersionsAsync());
        await Grants(actorId, Permissions.Chat.Access);
        Assert.Equal(png, (await pictures.GetAsync(actorId))!.Value.Data);
        await Grants(actorId);
        await pictures.RemoveAsync();
        await using var db = database.CreateDbContext();
        Assert.False(await db.UserProfilePictures.AnyAsync(x => x.UserId == actorId));
    }

    [Theory]
    [InlineData("anonymous")][InlineData("inactive")][InlineData("stamp")]
    public async Task InvalidActors_FailBeforeChatContent(string mode)
    {
        if (mode == "anonymous") actor.Principal = new(new System.Security.Claims.ClaimsIdentity());
        else
        {
            await using var db = database.CreateDbContext();
            if (mode == "inactive") await db.Users.Where(x => x.Id == actorId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            else await db.Users.Where(x => x.Id == actorId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, Guid.NewGuid().ToString()));
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).GetThreadAsync(channel));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).DeleteOthersMessageAsync(otherMessage, "Denied"));
    }

    private sealed class FixedUser(string id) : ICurrentUser
    {
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>(id);
    }
}
