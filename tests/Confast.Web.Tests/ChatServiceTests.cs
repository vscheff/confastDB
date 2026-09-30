using Confast.Web.Features.Chat;
using Confast.Web.Features.Identity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChatServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly ChatNotifications notifications = new(NullLogger<ChatNotifications>.Instance);

    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private ChatService Service(string userId) =>
        new(database, new TestCurrentUser(userId), notifications, TimeProvider.System);

    private ChatService Service(string userId, TimeProvider timeProvider) =>
        new(database, new TestCurrentUser(userId), notifications, timeProvider);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(string Owner, string Member, string Outsider)> UsersAsync()
    {
        var ids = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await using var db = database.CreateDbContext();
        db.Users.AddRange(
            new ApplicationUser { Id = ids.Item1, UserName = "owner", DisplayName = "Owner", IsActive = true },
            new ApplicationUser { Id = ids.Item2, UserName = "member", DisplayName = "Member", IsActive = true },
            new ApplicationUser { Id = ids.Item3, UserName = "outsider", DisplayName = "Outsider", IsActive = true });
        await db.SaveChangesAsync();
        return ids;
    }

    [Fact]
    public async Task ChannelThreads_AreScopedToChannelAndKeepPinsSeparate()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var channelId = await chat.CreateChannelAsync("Engineering", ChannelVisibility.Private, [member]);
        var otherChannelId = await chat.CreateChannelAsync("Other", ChannelVisibility.Private, [member]);
        var directId = await chat.OpenDirectAsync(member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).CreateChannelThreadAsync(channelId, "Forbidden", "No access"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).GetChannelThreadsAsync(channelId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).GetThreadAsync(channelId, channelThreadId: 123));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            chat.CreateChannelThreadAsync(directId, "Wrong place", "No channel"));

        var firstId = await chat.CreateChannelThreadAsync(channelId, "First thread", "First message");
        var secondId = await chat.CreateChannelThreadAsync(channelId, "Second thread", "Second message");
        var channelMessageId = await chat.SendAsync(channelId, "In the channel");
        var firstMessageId = await chat.SendAsync(channelId, "In the first thread", channelThreadId: firstId);
        var secondMessageId = await chat.SendAsync(channelId, "In the second thread", channelThreadId: secondId);
        await chat.SetMessagePinnedAsync(channelMessageId, true);
        await chat.SetMessagePinnedAsync(firstMessageId, true);
        await chat.SetMessagePinnedAsync(secondMessageId, true);

        var channel = await Service(member).GetThreadAsync(channelId);
        Assert.Equal(2, channel.Messages.Count(x => x.Type == ChatMessageType.ThreadNotice));
        Assert.DoesNotContain(channel.Messages, x => x.Id == firstMessageId || x.Id == secondMessageId);
        Assert.Equal(channelMessageId, Assert.Single(channel.PinnedMessages).Id);
        var first = await Service(member).GetThreadAsync(channelId, channelThreadId: firstId);
        Assert.Equal("First thread", first.ChannelThread?.Title);
        Assert.Contains(first.Messages, x => x.Id == firstMessageId);
        Assert.DoesNotContain(first.Messages, x => x.Id == secondMessageId);
        Assert.Equal(firstMessageId, Assert.Single(first.PinnedMessages).Id);
        Assert.Equal(secondMessageId, Assert.Single((await chat.GetThreadAsync(channelId,
            channelThreadId: secondId)).PinnedMessages).Id);
        var memberChat = Service(member);
        Assert.Equal((await memberChat.GetConversationsAsync()).Sum(x => x.UnreadCount),
            await memberChat.GetTotalUnreadCountAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.SendAsync(channelId, "cross thread", replyToMessageId: secondMessageId,
                channelThreadId: firstId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.SendAsync(otherChannelId, "cross channel", channelThreadId: firstId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.GetThreadAsync(otherChannelId, channelThreadId: firstId));
    }

    [Fact]
    public async Task ChannelThreads_ExpireAfterThreeDaysAndReviveOnReply()
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var chat = Service(owner, time);
        var channelId = await chat.CreateChannelAsync("Operations", ChannelVisibility.Private, [member]);
        var threadId = await chat.CreateChannelThreadAsync(channelId, "Shift handoff", "Initial handoff");
        Assert.Equal(threadId, Assert.Single(await chat.GetActiveChannelThreadsAsync()).Id);

        time.Now = time.Now.AddDays(3).AddSeconds(1);
        Assert.Empty(await chat.GetActiveChannelThreadsAsync());
        Assert.Equal(threadId, Assert.Single(await chat.GetChannelThreadsAsync(channelId)).Id);
        await Service(member, time).SendAsync(channelId, "New update", channelThreadId: threadId);
        Assert.Equal(threadId, Assert.Single(await chat.GetActiveChannelThreadsAsync()).Id);
    }

    [Fact]
    public async Task ChannelThread_CanStartFromAnExistingChannelReply()
    {
        var (owner, member, _) = await UsersAsync();
        var chat = Service(owner);
        var channelId = await chat.CreateChannelAsync("Production", ChannelVisibility.Private, [member]);
        var originalId = await chat.SendAsync(channelId, "Original");
        var replyId = await chat.SendAsync(channelId, "The follow-up", replyToMessageId: originalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.CreateChannelThreadAsync(channelId, "Invalid source", "Initial message", startedFromMessageId: originalId));
        var threadId = await chat.CreateChannelThreadAsync(channelId, "Follow-up", "More detail",
            startedFromMessageId: replyId);
        var thread = await Service(member).GetThreadAsync(channelId, channelThreadId: threadId);
        Assert.Equal(replyId, thread.ChannelThread?.StartedFromMessageId);
        Assert.Equal("The follow-up", thread.ChannelThread?.StartedFromMessage);
    }

    [Fact]
    public async Task Attachment_RequiresMembershipAndDisappearsWhenMessageIsDeleted()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var conversationId = await sender.CreateChannelAsync("Files", ChannelVisibility.Private, [member]);
        var bytes = System.Text.Encoding.UTF8.GetBytes("A short note\nSecond line");
        var messageId = await sender.SendAsync(conversationId, "", attachmentFileName: "note.txt",
            attachmentContent: bytes);

        var message = Assert.Single((await Service(member).GetThreadAsync(conversationId)).Messages);
        Assert.Equal(ChatAttachmentKind.Text, message.Attachment?.Kind);
        Assert.Equal("A short note\nSecond line", message.Attachment?.Text);
        Assert.Equal(bytes, (await Service(member).GetAttachmentAsync(messageId))?.Content);
        Assert.Null(await Service(outsider).GetAttachmentAsync(messageId));
        await sender.DeleteOwnMessageAsync(messageId);
        Assert.Null(await Service(member).GetAttachmentAsync(messageId));
    }

    [Fact]
    public async Task DirectConversation_ReusesTheSamePairIncludingConcurrentOpens()
    {
        var (owner, member, outsider) = await UsersAsync();
        var first = Service(owner);
        var second = Service(member);
        var ids = await Task.WhenAll(first.OpenDirectAsync(member), second.OpenDirectAsync(owner));
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(ids[0], await first.OpenDirectAsync(member));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.OpenDirectAsync(owner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).GetThreadAsync(ids[0]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).SendAsync(ids[0], "forged"));
        await using var db = database.CreateDbContext();
        Assert.Single(db.ChatConversations);
        Assert.Equal(2, db.ChatConversationMembers.Count());
    }

    [Fact]
    public async Task PrivateChannel_IsInvisibleAndInaccessibleToNonMembers()
    {
        var (owner, member, outsider) = await UsersAsync();
        var id = await Service(owner).CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var other = Service(outsider);
        Assert.Empty(await other.GetConversationsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.GetThreadAsync(id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.SendAsync(id, "forged"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).AddPrivateMemberAsync(id, outsider));
        Assert.Single(await Service(member).GetConversationsAsync());
    }

    [Fact]
    public async Task ChannelGroups_ExposeOnlyMemberChannelsAndProtectHiddenChannels()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var shared = await chat.CreateChannelAsync("Shared", ChannelVisibility.Private, [member]);
        var hidden = await chat.CreateChannelAsync("Hidden", ChannelVisibility.Private, [outsider]);
        var groupId = await chat.CreateChannelGroupAsync("Operations", shared);
        await chat.MoveChannelAsync(hidden, groupId, null);

        var nonMemberId = Guid.NewGuid().ToString();
        await using (var db = database.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = nonMemberId, UserName = "nonmember", DisplayName = "Nonmember", IsActive = true
            });
            await db.SaveChangesAsync();
        }
        Assert.Empty(await Service(nonMemberId).GetChannelGroupsAsync());
        var memberGroup = Assert.Single(await Service(member).GetChannelGroupsAsync());
        Assert.Equal(groupId, memberGroup.Id);
        Assert.False(memberGroup.CanManage);
        Assert.Equal([shared], (await Service(member).GetConversationsAsync())
            .Where(x => x.Kind == ConversationKind.Channel).Select(x => x.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).RenameChannelGroupAsync(groupId, "Other"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).MoveChannelAsync(hidden, null, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).MoveChannelAsync(shared, null, null));

        var separate = await Service(nonMemberId).CreateChannelAsync("Separate", ChannelVisibility.Private, []);
        var separateGroup = await Service(nonMemberId).CreateChannelGroupAsync("Operations", separate);
        Assert.NotEqual(groupId, separateGroup);
        Assert.Single(await Service(nonMemberId).GetChannelGroupsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).MoveChannelAsync(shared, separateGroup, null));

        var direct = await chat.OpenDirectAsync(member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.MoveChannelAsync(direct, groupId, null));
    }

    [Fact]
    public async Task ChannelLayout_PersistsGroupAndChannelOrderAcrossUsers()
    {
        var (owner, member, _) = await UsersAsync();
        var chat = Service(owner);
        var first = await chat.CreateChannelAsync("First", ChannelVisibility.Public, []);
        var second = await chat.CreateChannelAsync("Second", ChannelVisibility.Public, []);
        var third = await chat.CreateChannelAsync("Third", ChannelVisibility.Public, []);
        var groupId = await chat.CreateChannelGroupAsync("Team", second);
        await chat.MoveChannelAsync(third, groupId, second);
        await chat.MoveChannelAsync(first, groupId, null);

        var memberChat = Service(member);
        Assert.Equal([third, second, first], (await memberChat.GetConversationsAsync())
            .Where(x => x.ChannelGroupId == groupId)
            .OrderBy(x => x.ChannelSortOrder).Select(x => x.Id));
        Assert.Equal("Team", Assert.Single(await memberChat.GetChannelGroupsAsync()).Name);

        await memberChat.MoveChannelAsync(first, null, null);
        await memberChat.RenameChannelGroupAsync(groupId, "Work");
        Assert.Equal("Work", Assert.Single(await chat.GetChannelGroupsAsync()).Name);
        var otherGroupId = await chat.CreateChannelGroupAsync("Other", first);
        await memberChat.MoveChannelGroupAsync(otherGroupId, groupId);
        Assert.Equal([otherGroupId, groupId], (await chat.GetChannelGroupsAsync()).Select(x => x.Id));
        await memberChat.DeleteChannelGroupAsync(groupId);
        await memberChat.DeleteChannelGroupAsync(otherGroupId);
        Assert.Empty(await chat.GetChannelGroupsAsync());
        Assert.All((await chat.GetConversationsAsync()).Where(x => x.Kind == ConversationKind.Channel),
            row => Assert.Null(row.ChannelGroupId));

        var oldGroup = await chat.CreateChannelGroupAsync("Old", first);
        var replacement = await chat.CreateChannelGroupAsync("Replacement", first);
        Assert.Equal([oldGroup, replacement], (await chat.GetChannelGroupsAsync()).Select(x => x.Id));
        Assert.Equal(replacement, Assert.Single(await memberChat.GetChannelGroupsAsync()).Id);
    }

    [Fact]
    public async Task EmptyChannelFolder_IsVisibleOnlyToItsCreatorUntilPopulated()
    {
        var (owner, member, _) = await UsersAsync();
        var ownerChat = Service(owner);
        var memberChat = Service(member);
        var channelId = await ownerChat.CreateChannelAsync("Shared", ChannelVisibility.Private, [member]);
        var folderId = await ownerChat.CreateChannelGroupAsync("New folder");

        Assert.True(Assert.Single(await ownerChat.GetChannelGroupsAsync()).CanManage);
        Assert.Empty(await memberChat.GetChannelGroupsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => memberChat.MoveChannelAsync(channelId, folderId, null));

        await ownerChat.MoveChannelAsync(channelId, folderId, null);
        Assert.Equal(folderId, Assert.Single(await memberChat.GetChannelGroupsAsync()).Id);
        await ownerChat.MoveChannelAsync(channelId, null, null);
        Assert.Empty(await memberChat.GetChannelGroupsAsync());
        Assert.Equal(folderId, Assert.Single(await ownerChat.GetChannelGroupsAsync()).Id);
        await ownerChat.DeleteChannelGroupAsync(folderId);
        Assert.Empty(await ownerChat.GetChannelGroupsAsync());
    }

    [Fact]
    public async Task CreateChannelInFolder_AssignsDestinationAndRequiresFolderAccess()
    {
        var (owner, member, _) = await UsersAsync();
        var ownerChat = Service(owner);
        var folderId = await ownerChat.CreateChannelGroupAsync("Operations");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).CreateChannelAsync("Not allowed", ChannelVisibility.Private, [], folderId));
        Assert.Empty(await Service(member).GetConversationsAsync());

        var channelId = await ownerChat.CreateChannelAsync("New channel", ChannelVisibility.Private, [member], folderId);
        Assert.Equal(folderId, (await ownerChat.GetConversationsAsync()).Single(x => x.Id == channelId).ChannelGroupId);
        Assert.Equal(folderId, Assert.Single(await Service(member).GetChannelGroupsAsync()).Id);
    }

    [Fact]
    public async Task NestedFolders_PersistOrderAndRejectCycles()
    {
        var (owner, member, _) = await UsersAsync();
        var chat = Service(owner);
        var first = await chat.CreateChannelAsync("First", ChannelVisibility.Private, [member]);
        var second = await chat.CreateChannelAsync("Second", ChannelVisibility.Private, [member]);
        var parent = await chat.CreateChannelGroupAsync("Parent", first);
        var child = await chat.CreateChannelGroupAsync("Child", parentGroupId: parent);
        var grandchild = await chat.CreateChannelGroupAsync("Grandchild", parentGroupId: child);
        await chat.MoveLayoutItemAsync(false, second, grandchild, null, null);
        await chat.MoveLayoutItemAsync(true, child, parent, false, first);

        var groups = await Service(member).GetChannelGroupsAsync();
        Assert.Equal(parent, groups.Single(x => x.Id == child).ParentGroupId);
        Assert.Equal(child, groups.Single(x => x.Id == grandchild).ParentGroupId);
        Assert.True(groups.Single(x => x.Id == child).SortOrder <
            (await chat.GetConversationsAsync()).Single(x => x.Id == first).ChannelSortOrder);
        Assert.Equal(grandchild, (await chat.GetConversationsAsync()).Single(x => x.Id == second).ChannelGroupId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.MoveLayoutItemAsync(true, parent, grandchild, null, null));
        Assert.Null((await chat.GetChannelGroupsAsync()).Single(x => x.Id == parent).ParentGroupId);
    }

    [Fact]
    public async Task NestedFolders_HidePrivateBranchesAndPromoteChildrenOnDelete()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var shared = await chat.CreateChannelAsync("Shared", ChannelVisibility.Private, [member]);
        var hidden = await chat.CreateChannelAsync("Hidden", ChannelVisibility.Private, [outsider]);
        var parent = await chat.CreateChannelGroupAsync("Parent");
        var visibleChild = await chat.CreateChannelGroupAsync("Visible", shared, parent);
        var hiddenChild = await chat.CreateChannelGroupAsync("Hidden folder", hidden, parent);

        var memberGroups = await Service(member).GetChannelGroupsAsync();
        Assert.Contains(memberGroups, x => x.Id == parent && !x.CanManage);
        Assert.Contains(memberGroups, x => x.Id == visibleChild);
        Assert.DoesNotContain(memberGroups, x => x.Id == hiddenChild);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(member).CreateChannelGroupAsync("Parent"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).MoveLayoutItemAsync(true, parent, null, null, null));

        await chat.DeleteChannelGroupAsync(parent);
        var ownerGroups = await chat.GetChannelGroupsAsync();
        Assert.DoesNotContain(ownerGroups, x => x.Id == parent);
        Assert.All(ownerGroups.Where(x => x.Id == visibleChild || x.Id == hiddenChild),
            x => Assert.Null(x.ParentGroupId));
        Assert.Equal(visibleChild, (await chat.GetConversationsAsync()).Single(x => x.Id == shared).ChannelGroupId);
    }

    [Fact]
    public async Task PublicChannel_IncludesAllActiveUsersAndEnrollsNewUsersAutomatically()
    {
        var (owner, member, outsider) = await UsersAsync();
        var id = await Service(owner).CreateChannelAsync("Production", ChannelVisibility.Public, []);
        await using (var db = database.CreateDbContext())
            Assert.Equal(3, db.ChatConversationMembers.Count(x => x.ConversationId == id));
        Assert.Equal(id, Assert.Single(await Service(member).GetConversationsAsync()).Id);
        Assert.Equal(id, Assert.Single(await Service(outsider).GetConversationsAsync()).Id);

        await Service(owner).SendAsync(id, "Earlier message");
        var newUserId = Guid.NewGuid().ToString();
        await using (var db = database.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = newUserId, UserName = "newcomer", DisplayName = "Newcomer", IsActive = true
            });
            await db.SaveChangesAsync();
        }
        var newcomer = Service(newUserId);
        Assert.Equal(id, Assert.Single(await newcomer.GetConversationsAsync()).Id);
        Assert.Equal(0, (await newcomer.GetConversationsAsync()).Single().UnreadCount);
        Assert.Equal("Earlier message", (await newcomer.GetThreadAsync(id)).Messages.Single().Body);
        await newcomer.SendAsync(id, "Hello everyone");
        await using (var db = database.CreateDbContext())
            Assert.Equal(4, db.ChatConversationMembers.Count(x => x.ConversationId == id));
    }

    [Fact]
    public async Task PrivateOwner_CanAddAndRemoveMembersAndRemovalRevokesAccess()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerService = Service(owner);
        var id = await ownerService.CreateChannelAsync("Issue", ChannelVisibility.Private, [member]);
        await ownerService.AddPrivateMemberAsync(id, outsider);
        await ownerService.AddPrivateMemberAsync(id, outsider);
        Assert.Single(await Service(outsider).GetConversationsAsync());
        await ownerService.RemovePrivateMemberAsync(id, outsider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).GetThreadAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ownerService.RemovePrivateMemberAsync(id, owner));
    }

    [Fact]
    public async Task PrivateChannelSettings_RequireMembershipAndOwnerForChanges()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerService = Service(owner);
        var id = await ownerService.CreateChannelAsync("Initial", ChannelVisibility.Private, [member]);

        Assert.Equal(new[] { owner, member }.OrderBy(x => x), (await Service(member).GetChannelMembersAsync(id))
            .Select(x => x.Id).OrderBy(x => x));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).GetChannelMembersAsync(id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).RenameChannelAsync(id, "Wrong"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).RenameChannelAsync(id, "Wrong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ownerService.RenameChannelAsync(id, " "));

        await ownerService.RenameChannelAsync(id, "  Revised  ");
        Assert.Equal("Revised", (await Service(member).GetConversationsAsync()).Single().Name);
        await ownerService.AddPrivateMemberAsync(id, outsider);
        Assert.Equal(3, (await ownerService.GetChannelMembersAsync(id)).Count);
        await ownerService.RemovePrivateMemberAsync(id, outsider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).GetChannelMembersAsync(id));

        var directId = await ownerService.OpenDirectAsync(member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ownerService.RenameChannelAsync(directId, "Wrong"));
    }

    [Fact]
    public async Task Administrator_CanRenameChannelsWithoutOwningThem()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerService = Service(owner);
        var channelId = await ownerService.CreateChannelAsync("First name", ChannelVisibility.Public, []);
        var privateChannelId = await Service(outsider).CreateChannelAsync("Private name", ChannelVisibility.Private, []);
        Assert.False((await Service(member).GetConversationsAsync()).Single().IsOwner);
        Assert.False(await Service(member).IsCurrentUserAdministratorAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).RenameChannelAsync(channelId, "Not allowed"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).RenameChannelAsync(privateChannelId, "Not allowed"));

        await using (var db = database.CreateDbContext())
        {
            db.UserRoles.Add(new()
            {
                UserId = member,
                RoleId = db.Roles.Single(x => x.Name == AppRoles.Administrator).Id
            });
            await db.SaveChangesAsync();
        }

        Assert.True(await Service(member).IsCurrentUserAdministratorAsync());
        await Service(member).RenameChannelAsync(channelId, "  Updated by administrator  ");
        Assert.Equal("Updated by administrator", (await Service(owner).GetConversationsAsync()).Single().Name);
        await Service(member).RenameChannelAsync(privateChannelId, "Updated private name");
        Assert.Equal("Updated private name", (await Service(outsider).GetConversationsAsync())
            .Single(x => x.Id == privateChannelId).Name);

        await using (var db = database.CreateDbContext())
        {
            db.UserRoles.Remove(db.UserRoles.Single(x => x.UserId == member));
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).RenameChannelAsync(channelId, "After role removal"));
    }

    [Fact]
    public async Task Messages_TrackUnreadAndReadProgressAndKeepDeletedAudit()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var id = await sender.OpenDirectAsync(member);
        var messageId = await sender.SendAsync(id, "Hello");
        Assert.Equal(1, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        Assert.True(await recipient.HasUnreadAlertAsync());
        Assert.False(await sender.HasUnreadAlertAsync());
        Assert.Equal(0, (await sender.GetConversationsAsync()).Single().UnreadCount);
        Assert.Equal("Hello", (await recipient.GetThreadAsync(id)).Messages.Single().Body);
        await recipient.MarkReadAsync(id, messageId);
        Assert.Equal(0, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        Assert.False(await recipient.HasUnreadAlertAsync());
        var secondMessageId = await sender.SendAsync(id, "Again");
        await Task.WhenAll(
            recipient.MarkReadAsync(id, secondMessageId),
            recipient.MarkReadAsync(id, messageId));
        Assert.Equal(0, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recipient.DeleteOwnMessageAsync(messageId));
        await sender.DeleteOwnMessageAsync(messageId);
        Assert.DoesNotContain((await recipient.GetThreadAsync(id)).Messages, message => message.Id == messageId);
        await using var db = database.CreateDbContext();
        var stored = await db.ChatMessages.FindAsync(messageId);
        Assert.Equal("Hello", stored!.Body);
        Assert.NotNull(stored.DeletedAtUtc);
        Assert.Equal(owner, stored.DeletedByUserId);
    }

    [Fact]
    public async Task DeletingAnUnreadMessageClearsItsUnreadCountAndAlert()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var id = await sender.OpenDirectAsync(member);
        var messageId = await sender.SendAsync(id, "Soon deleted");
        Assert.Equal(1, await recipient.GetTotalUnreadCountAsync());

        await sender.DeleteOwnMessageAsync(messageId);

        Assert.Empty((await recipient.GetThreadAsync(id)).Messages);
        Assert.Equal(0, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        Assert.Equal(0, await recipient.GetTotalUnreadCountAsync());
        Assert.False(await recipient.HasUnreadAlertAsync());
    }

    [Fact]
    public async Task Pinning_PersistsNoticeAndPinnedList_AndDeletionClearsThePin()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerChat = Service(owner);
        var memberChat = Service(member);
        var channelId = await ownerChat.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var messageId = await ownerChat.SendAsync(channelId, "Keep this handy");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).SetMessagePinnedAsync(messageId, true));
        await memberChat.SetMessagePinnedAsync(messageId, true);
        await memberChat.SetMessagePinnedAsync(messageId, true);
        Assert.False((await ownerChat.GetConversationsAsync()).Single(x => x.Id == channelId).HasUnreadMention);

        var thread = await ownerChat.GetThreadAsync(channelId);
        var pinned = Assert.Single(thread.PinnedMessages);
        Assert.Equal(messageId, pinned.Id);
        Assert.Equal("Keep this handy", pinned.Body);
        Assert.NotNull(thread.Messages.Single(x => x.Id == messageId).PinnedAtUtc);
        var notice = Assert.Single(thread.Messages, x => x.Type == ChatMessageType.PinNotice);
        Assert.Equal(member, notice.SenderUserId);
        Assert.Equal(messageId, notice.ReplyToMessageId);
        Assert.Equal("Keep this handy", notice.ReplyToBody);

        await ownerChat.SetMessagePinnedAsync(messageId, false);
        Assert.Empty((await ownerChat.GetThreadAsync(channelId)).PinnedMessages);
        await ownerChat.SetMessagePinnedAsync(messageId, true);
        Assert.Equal(2, (await ownerChat.GetThreadAsync(channelId)).Messages.Count(x => x.Type == ChatMessageType.PinNotice));
        await ownerChat.DeleteOwnMessageAsync(messageId);
        Assert.Empty((await memberChat.GetThreadAsync(channelId)).PinnedMessages);
        await using var db = database.CreateDbContext();
        var stored = await db.ChatMessages.FindAsync(messageId);
        Assert.Null(stored!.PinnedAtUtc);
        Assert.Null(stored.PinnedByUserId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => memberChat.SetMessagePinnedAsync(messageId, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).SetMessagePinnedAsync(messageId, false));
    }

    [Fact]
    public async Task Replies_KeepTheirTargetAndAlertTheOriginalSender()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerChat = Service(owner);
        var memberChat = Service(member);
        var channelId = await ownerChat.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var otherChannelId = await ownerChat.CreateChannelAsync("Other", ChannelVisibility.Private, [member]);
        var originalId = await ownerChat.SendAsync(channelId, "A long original message");
        var otherId = await ownerChat.SendAsync(otherChannelId, "Other channel");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memberChat.SendAsync(channelId, "Wrong target", replyToMessageId: otherId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).SendAsync(channelId, "Unauthorized", replyToMessageId: originalId));

        var replyId = await memberChat.SendAsync(channelId, "I checked it", replyToMessageId: originalId);
        var reply = (await ownerChat.GetThreadAsync(channelId)).Messages.Single(x => x.Id == replyId);
        Assert.Equal(originalId, reply.ReplyToMessageId);
        Assert.Equal(owner, reply.ReplyToSenderUserId);
        Assert.Equal("Owner", reply.ReplyToSenderName);
        Assert.Equal("A long original message", reply.ReplyToBody);
        Assert.True((await ownerChat.GetConversationsAsync()).Single(x => x.Id == channelId).HasUnreadMention);
        Assert.True(await ownerChat.HasUnreadAlertAsync());
        await ownerChat.MarkReadAsync(channelId, replyId);
        Assert.False(await ownerChat.HasUnreadAlertAsync());

        await ownerChat.DeleteOwnMessageAsync(originalId);
        reply = (await memberChat.GetThreadAsync(channelId)).Messages.Single(x => x.Id == replyId);
        Assert.Null(reply.ReplyToBody);
        Assert.DoesNotContain((await memberChat.GetThreadAsync(channelId)).Messages, message => message.Id == originalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memberChat.SendAsync(channelId, "Deleted target", replyToMessageId: originalId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ownerChat.GetThreadAsync(channelId, throughMessageId: originalId));
    }

    [Fact]
    public async Task MarkUnread_StartsAtSelectedMessageAndCannotCrossConversations()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var otherChannelId = await sender.CreateChannelAsync("Other", ChannelVisibility.Private, [member]);
        var firstId = await sender.SendAsync(channelId, "First");
        var secondId = await sender.SendAsync(channelId, "Second");
        var thirdId = await sender.SendAsync(channelId, "Third");
        var otherId = await sender.SendAsync(otherChannelId, "Other");
        await recipient.MarkReadAsync(channelId, thirdId);

        await recipient.MarkUnreadAsync(channelId, secondId);
        Assert.Equal(2, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);
        await recipient.MarkUnreadAsync(channelId, thirdId);
        Assert.Equal(2, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recipient.MarkUnreadAsync(channelId, otherId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).MarkUnreadAsync(channelId, firstId));
        await recipient.MarkUnreadAsync(channelId, firstId);
        Assert.Equal(3, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);
        await recipient.MarkReadAsync(channelId, thirdId);
        Assert.Equal(3, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);
        await recipient.MarkConversationReadAsync(channelId);
        Assert.Equal(0, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);

        await sender.MarkReadAsync(channelId, thirdId);
        await sender.MarkUnreadAsync(channelId, thirdId);
        var ownMessageUnread = (await sender.GetConversationsAsync()).Single(x => x.Id == channelId);
        Assert.True(ownMessageUnread.IsManuallyUnread);
        Assert.Equal(1, ownMessageUnread.UnreadCount);
        Assert.Equal(1, await sender.GetTotalUnreadCountAsync());
        await sender.SendAsync(channelId, "Back in the conversation");
        Assert.False((await sender.GetConversationsAsync()).Single(x => x.Id == channelId).IsManuallyUnread);
        Assert.Equal(0, (await sender.GetConversationsAsync()).Single(x => x.Id == channelId).UnreadCount);

        var directId = await sender.OpenDirectAsync(member);
        var ownDirectMessageId = await sender.SendAsync(directId, "Direct update");
        await sender.MarkReadAsync(directId, ownDirectMessageId);
        await sender.MarkUnreadAsync(directId, ownDirectMessageId);
        Assert.True(await sender.HasUnreadAlertAsync());
        Assert.Equal(1, (await sender.GetConversationsAsync()).Single(x => x.Id == directId).UnreadCount);
    }

    [Fact]
    public async Task ChannelMentions_OnlyTargetMembers_AndUnreadBadgesPreferMentions()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);

        await sender.SendAsync(channelId, "Normal update");
        var row = Assert.Single(await recipient.GetConversationsAsync());
        Assert.Equal(1, row.UnreadCount);
        Assert.False(row.HasUnreadMention);
        Assert.False(await recipient.HasUnreadAlertAsync());

        var taggedMessageId = await sender.SendAsync(channelId, "Please check this, @mEmBeR");
        row = Assert.Single(await recipient.GetConversationsAsync());
        Assert.Equal(2, row.UnreadCount);
        Assert.True(row.HasUnreadMention);
        Assert.True(await recipient.HasUnreadAlertAsync());
        Assert.True(await recipient.HasUnreadMentionAsync());
        Assert.False(await Service(outsider).HasUnreadMentionAsync());
        var tagged = (await recipient.GetThreadAsync(channelId)).Messages.Single(x => x.Id == taggedMessageId);
        Assert.Equal("Please check this, @Member", tagged.Body);
        Assert.Contains(tagged.Parts, part => part.IsTag && part.UserId == member && part.Text == "@Member");

        await recipient.MarkReadAsync(channelId, taggedMessageId);
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        Assert.False(await recipient.HasUnreadAlertAsync());
        var outsiderMessageId = await sender.SendAsync(channelId, "@Outsider cannot be tagged here");
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        var outsiderMessage = (await recipient.GetThreadAsync(channelId)).Messages.Single(x => x.Id == outsiderMessageId);
        Assert.DoesNotContain(outsiderMessage.Parts, part => part.IsTag);

        var everyoneId = await sender.SendAsync(channelId, "@EvErYoNe please review");
        Assert.Equal("@everyone please review", (await recipient.GetThreadAsync(channelId)).Messages.Single(x => x.Id == everyoneId).Body);
        Assert.True((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        await sender.DeleteOwnMessageAsync(everyoneId);
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);

        var editableId = await sender.SendAsync(channelId, "Initial text");
        await sender.EditOwnMessageAsync(editableId, "Now tagged: @mEmBeR");
        Assert.Equal("Now tagged: @Member", (await recipient.GetThreadAsync(channelId)).Messages.Single(x => x.Id == editableId).Body);
        Assert.True((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        await sender.EditOwnMessageAsync(editableId, "No tag now");
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
    }

    [Fact]
    public async Task HereMentions_UseLiveVisiblePresenceAtSendTime()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);

        var offlineId = await sender.SendAsync(channelId, "@here offline");
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        await recipient.MarkReadAsync(channelId, offlineId);

        await using (var db = database.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            db.UserPresenceSessions.Add(new UserPresenceSession
            {
                Id = Guid.NewGuid(), UserId = member, LastHeartbeatAtUtc = now,
                LastActivityAtUtc = now
            });
            await db.SaveChangesAsync();
        }
        var onlineId = await sender.SendAsync(channelId, "@here online");
        Assert.True((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        await recipient.MarkReadAsync(channelId, onlineId);

        await using (var db = database.CreateDbContext())
        {
            var user = await db.Users.FindAsync(member);
            user!.PresencePreference = UserPresencePreference.Invisible;
            await db.SaveChangesAsync();
        }
        await sender.SendAsync(channelId, "@here invisible");
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
    }

    [Fact]
    public async Task SavedTagStillTargetsOriginalUserAfterChannelMembershipAndNamesChange()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var messageId = await sender.SendAsync(channelId, "Check with @Member");

        await sender.RemovePrivateMemberAsync(channelId, member);
        await using (var db = database.CreateDbContext())
        {
            (await db.Users.FindAsync(member))!.DisplayName = "Renamed";
            (await db.Users.FindAsync(outsider))!.DisplayName = "Member";
            await db.SaveChangesAsync();
        }
        await sender.AddPrivateMemberAsync(channelId, outsider);

        var message = (await sender.GetThreadAsync(channelId)).Messages.Single(x => x.Id == messageId);
        var tag = Assert.Single(message.Parts, x => x.IsTag);
        Assert.Equal("@Member", tag.Text);
        Assert.Equal(member, tag.UserId);
        Assert.Equal("Renamed", tag.Name);
    }

    [Fact]
    public async Task EarlierMentionRecipientsCanStillRenderTheirTag()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var messageId = await sender.SendAsync(channelId, "Check with @Member");
        await using (var db = database.CreateDbContext())
        {
            db.ChatMessageTags.RemoveRange(db.ChatMessageTags.Where(x => x.MessageId == messageId));
            await db.SaveChangesAsync();
        }

        var message = (await sender.GetThreadAsync(channelId)).Messages.Single(x => x.Id == messageId);
        Assert.Contains(message.Parts, part => part.IsTag && part.UserId == member);
    }

    [Fact]
    public async Task Reactions_AggregateUsers_AllowMultipleEmoji_AndToggleOnlyTheCurrentUsersVote()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var conversationId = await sender.OpenDirectAsync(member);
        var messageId = await sender.SendAsync(conversationId, "React here");

        await sender.AddReactionAsync(messageId, "👍");
        await sender.AddReactionAsync(messageId, "👍");
        await sender.AddReactionAsync(messageId, "❤️");
        await recipient.AddReactionAsync(messageId, "👍");
        var reactions = (await sender.GetThreadAsync(conversationId)).Messages.Single().Reactions;
        Assert.Equal(new[] { "👍", "❤️" }, reactions.Select(x => x.Emoji));
        Assert.Equal(new[] { "Owner", "Member" }, reactions[0].Users.Select(x => x.Name));
        Assert.Single(reactions[1].Users);

        await recipient.ToggleReactionAsync(messageId, "👍");
        reactions = (await sender.GetThreadAsync(conversationId)).Messages.Single().Reactions;
        Assert.Equal("Owner", Assert.Single(reactions[0].Users).Name);
        await recipient.ToggleReactionAsync(messageId, "👍");
        Assert.Equal(2, (await sender.GetThreadAsync(conversationId)).Messages.Single().Reactions[0].Users.Count);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).AddReactionAsync(messageId, "👍"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.AddReactionAsync(messageId, "not emoji"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.AddReactionAsync(messageId, "👍👍"));
        await sender.DeleteOwnMessageAsync(messageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recipient.ToggleReactionAsync(messageId, "👍"));
    }

    [Fact]
    public async Task LastReactionEmoji_IsPerUserAndSurvivesRemovalAndMessageDeletion()
    {
        var (owner, member, _) = await UsersAsync();
        var first = Service(owner);
        var second = Service(member);
        var conversationId = await first.OpenDirectAsync(member);
        var messageId = await first.SendAsync(conversationId, "Recent reaction");

        Assert.Null(await first.GetLastReactionEmojiAsync());
        Assert.Null(await second.GetLastReactionEmojiAsync());
        await first.AddReactionAsync(messageId, "👍🏽");
        Assert.Null(await first.GetLastReactionEmojiAsync());
        await first.AddReactionAsync(messageId, "🎉");
        await first.AddReactionAsync(messageId, "👎🏻");
        await first.ToggleReactionAsync(messageId, "👍");
        Assert.Equal("🎉", await first.GetLastReactionEmojiAsync());
        await second.ToggleReactionAsync(messageId, "❤️");
        await second.AddReactionAsync(messageId, "👍🏿");
        Assert.Equal("❤️", await second.GetLastReactionEmojiAsync());

        await first.ToggleReactionAsync(messageId, "🎉");
        await second.ToggleReactionAsync(messageId, "❤️");
        Assert.Equal("🎉", await first.GetLastReactionEmojiAsync());
        Assert.Equal("❤️", await second.GetLastReactionEmojiAsync());

        await first.DeleteOwnMessageAsync(messageId);
        Assert.Equal("🎉", await first.GetLastReactionEmojiAsync());
    }

    [Fact]
    public async Task EmojiTonePreferences_ArePerUserPerEmoji_AndCanReturnToDefault()
    {
        var (owner, member, _) = await UsersAsync();
        var first = Service(owner);
        var second = Service(member);

        Assert.Empty(await first.GetEmojiTonePreferencesAsync());
        await first.SetEmojiTonePreferenceAsync("👍", "👍🏽");
        await first.SetEmojiTonePreferenceAsync("👎", "👎🏻");
        await second.SetEmojiTonePreferenceAsync("👍", "👍🏿");
        Assert.Equal("👍🏽", (await first.GetEmojiTonePreferencesAsync())["👍"]);
        Assert.Equal("👎🏻", (await first.GetEmojiTonePreferencesAsync())["👎"]);
        Assert.Equal("👍🏿", (await second.GetEmojiTonePreferencesAsync())["👍"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            first.SetEmojiTonePreferenceAsync("👍", "👎🏽"));
        await first.SetEmojiTonePreferenceAsync("👍", "👍");
        Assert.False((await first.GetEmojiTonePreferencesAsync()).ContainsKey("👍"));
        Assert.Equal("👍🏿", (await second.GetEmojiTonePreferencesAsync())["👍"]);
    }

    [Fact]
    public async Task GeneratedNeutralEmoji_CanBeUsedAsAReaction()
    {
        var (owner, member, _) = await UsersAsync();
        var service = Service(owner);
        var family = ChatEmojiCatalog.Families.First(x => x.Tones.Count > 1 &&
            !ChatEmojiCatalog.All.Any(option => option.Emoji == x.Default.Emoji));
        var conversationId = await service.OpenDirectAsync(member);
        var messageId = await service.SendAsync(conversationId, "Neutral reaction");

        await service.AddReactionAsync(messageId, family.Default.Emoji);
        Assert.Equal(family.Default.Emoji,
            Assert.Single((await service.GetThreadAsync(conversationId)).Messages.Single().Reactions).Emoji);
    }

    [Fact]
    public async Task ConcurrentReactionAdds_CountEachUserOnce()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var conversationId = await sender.OpenDirectAsync(member);
        var messageId = await sender.SendAsync(conversationId, "Concurrent votes");

        await Task.WhenAll(
            sender.AddReactionAsync(messageId, "🎉"),
            sender.AddReactionAsync(messageId, "🎉"),
            recipient.AddReactionAsync(messageId, "🎉"));

        var reaction = Assert.Single((await sender.GetThreadAsync(conversationId)).Messages.Single().Reactions);
        Assert.Equal(2, reaction.Users.Count);
    }

    [Fact]
    public async Task MarkConversationRead_AdvancesThroughMessagesReceivedAfterTheThreadWasLoaded()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var id = await sender.OpenDirectAsync(member);
        var firstMessageId = await sender.SendAsync(id, "First");

        await recipient.MarkReadAsync(id, firstMessageId);
        var arrivingMessageId = await sender.SendAsync(id, "Arrived while open");

        Assert.Equal(1, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        Assert.Equal(arrivingMessageId, await recipient.MarkConversationReadAsync(id));
        Assert.Equal(0, (await recipient.GetConversationsAsync()).Single().UnreadCount);
    }

    [Fact]
    public async Task UnreadCounts_AreScopedToEachUserWhenOneConversationMovesToTheTop()
    {
        var (firstUser, secondUser, _) = await UsersAsync();
        var first = Service(firstUser);
        var second = Service(secondUser);
        var directId = await first.OpenDirectAsync(secondUser);
        var channelId = await first.CreateChannelAsync("Updates", ChannelVisibility.Public, [secondUser]);

        await second.SendAsync(directId, "One");
        await second.SendAsync(directId, "Two");
        await second.SendAsync(directId, "Three");

        await first.SendAsync(channelId, "Channel update");

        var firstUserConversations = await first.GetConversationsAsync();
        var secondUserConversations = await second.GetConversationsAsync();
        Assert.Equal(3, firstUserConversations.Single(x => x.Id == directId).UnreadCount);
        Assert.Equal(0, firstUserConversations.Single(x => x.Id == channelId).UnreadCount);
        Assert.Equal(0, secondUserConversations.Single(x => x.Id == directId).UnreadCount);
        Assert.Equal(1, secondUserConversations.Single(x => x.Id == channelId).UnreadCount);
        Assert.Equal("Member", firstUserConversations.Single(x => x.Id == directId).Name);
        Assert.Equal("Owner", secondUserConversations.Single(x => x.Id == directId).Name);
        Assert.Equal(ConversationKind.Direct, firstUserConversations.Single(x => x.Id == directId).Kind);
        Assert.Equal(ConversationKind.Channel, firstUserConversations.Single(x => x.Id == channelId).Kind);
    }

    private sealed class TestCurrentUser(string userId) : ICurrentUser
    {
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>(userId);
    }
}
