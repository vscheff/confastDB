using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Confast.Web.Features.Chat;

public sealed record ChatUser(string Id, string Name, string? UserName = null);
public sealed record ChatConversationRow(long Id, ConversationKind Kind, ChannelVisibility? Visibility,
    string Name, DateTime ActivityAtUtc, int UnreadCount, bool HasUnreadMention, bool IsOwner,
    long? ChannelGroupId, int ChannelSortOrder, string? OtherUserId, bool IsManuallyUnread);
public sealed record ChatChannelGroupRow(long Id, string Name, long? ParentGroupId, int SortOrder, bool CanManage);
public sealed record ChatChannelThreadRow(long Id, long ConversationId, string Title, string CreatedByName,
    DateTime CreatedAtUtc, DateTime LastMessageAtUtc, int MessageCount, string? LatestMessage,
    long? StartedFromMessageId, string? StartedFromMessage);
public sealed record ChatMessageRow(long Id, ChatMessageType Type, string? SenderUserId, string SenderName, string? Body,
    DateTime SentAtUtc, DateTime? EditedAtUtc, bool IsDeleted, DateTime? PinnedAtUtc,
    IReadOnlyList<ChatReactionRow> Reactions, IReadOnlyList<ChatMessagePart> Parts,
    long? ReplyToMessageId, string? ReplyToSenderUserId, string? ReplyToSenderName, string? ReplyToBody,
    IReadOnlyList<ChatAttachmentRow> Attachments, long? ChannelThreadId = null, ChatPollRow? Poll = null);
public sealed record ChatAttachmentRow(long Id, string FileName, ChatAttachmentKind Kind, int Size, string? Text);
public sealed record ChatAttachmentUpload(string FileName, byte[] Content);
public sealed record ChatAttachmentFile(string FileName, string ContentType, ChatAttachmentKind Kind, byte[] Content);
public sealed record ChatReactionRow(string Emoji, IReadOnlyList<ChatUser> Users);
public sealed record ChatPinnedMessageRow(long Id, string SenderName, string Body, DateTime SentAtUtc);
public sealed record ChatThread(ChatConversationRow Conversation, IReadOnlyList<ChatMessageRow> Messages,
    IReadOnlyList<ChatUser> Members, IReadOnlyList<ChatPinnedMessageRow> PinnedMessages,
    ChatChannelThreadRow? ChannelThread = null);

// Blazor Interactive Server uses SignalR to deliver these callbacks to connected circuits.
// A reconnect always reloads persisted state; events are hints, not the source of truth.
public sealed class ChatNotifications(ILogger<ChatNotifications> logger)
{
    private readonly ConcurrentDictionary<Guid, (string UserId, Action Callback)> subscriptions = new();

    public IDisposable Subscribe(string userId, Action callback)
    {
        var key = Guid.NewGuid();
        subscriptions[key] = (userId, callback);
        return new Subscription(() => subscriptions.TryRemove(key, out _));
    }

    public void Publish(IEnumerable<string> userIds)
    {
        var recipients = userIds.ToHashSet(StringComparer.Ordinal);
        foreach (var (key, subscription) in subscriptions)
        {
            if (!recipients.Contains(subscription.UserId)) continue;
            try { subscription.Callback(); }
            catch (Exception ex)
            {
                // A disconnected circuit must not make a committed send look like a failure.
                subscriptions.TryRemove(key, out _);
                logger.LogWarning(ex, "A chat circuit could not receive a notification.");
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public sealed partial class ChatService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICurrentUser currentUser,
    ChatNotifications notifications,
    TimeProvider clock)
{
    public static readonly TimeSpan ChannelThreadActiveFor = TimeSpan.FromDays(3);
    private sealed record ResolvedMentions(string Body, IReadOnlySet<string> Recipients, IReadOnlyList<ChatMentionToken> Tags);

    private const string UnsupportedDatabaseEncodingMessage =
        "This database cannot store that character because it is not using UTF-8. An administrator must move the database to UTF-8 before this text can be saved.";

    private async Task<string> RequireUserAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = await currentUser.GetUserIdAsync();
        if (userId is null || !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken))
            throw new UnauthorizedAccessException("An active account is required for chat.");
        await EnsurePublicChannelMembershipsAsync(db, userId, clock.GetUtcNow().UtcDateTime, cancellationToken);
        return userId;
    }

    internal static Task EnsurePublicChannelMembershipsAsync(AppDbContext db, string userId,
        DateTime joinedAtUtc, CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_conversation_members
                (conversation_id, user_id, joined_at_utc, last_read_message_id, is_owner)
            SELECT channel.id, {userId}, {joinedAtUtc},
                (SELECT max(message.id) FROM chat_messages AS message WHERE message.conversation_id = channel.id),
                false
            FROM chat_conversations AS channel
            WHERE channel.kind = {(int)ConversationKind.Channel}
                AND channel.visibility = {(int)ChannelVisibility.Public}
                AND NOT EXISTS (
                    SELECT 1 FROM chat_conversation_members AS member
                    WHERE member.conversation_id = channel.id AND member.user_id = {userId})
            ON CONFLICT (conversation_id, user_id) DO NOTHING
            """, cancellationToken);

    public async Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await RequireUserAsync(db, cancellationToken);
    }

    public async Task<bool> IsCurrentUserAdministratorAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await IsAdministratorAsync(db, userId, cancellationToken);
    }

    private static Task<bool> IsAdministratorAsync(AppDbContext db, string userId,
        CancellationToken cancellationToken) =>
        (from userRole in db.UserRoles
         join role in db.Roles on userRole.RoleId equals role.Id
         where userRole.UserId == userId && role.Name == AppRoles.Administrator
         select userRole).AnyAsync(cancellationToken);

    public async Task<string?> GetLastReactionEmojiAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var emoji = await db.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => x.LastReactionEmoji).SingleAsync(cancellationToken);
        return emoji is not null && IsThumbReaction(emoji) ? null : emoji;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetEmojiTonePreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await db.ChatEmojiTonePreferences.AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToDictionaryAsync(x => x.DefaultEmoji, x => x.PreferredEmoji, cancellationToken);
    }

    public async Task SetEmojiTonePreferenceAsync(string defaultEmoji, string preferredEmoji,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var family = ChatEmojiCatalog.FindFamily(defaultEmoji);
        if (family is null || family.Tones.Count < 2 || !ChatEmojiCatalog.IsToneOf(defaultEmoji, preferredEmoji))
            throw new InvalidOperationException("Choose a skin tone from this emoji's options.");
        if (preferredEmoji == defaultEmoji)
        {
            await db.ChatEmojiTonePreferences
                .Where(x => x.UserId == userId && x.DefaultEmoji == defaultEmoji)
                .ExecuteDeleteAsync(cancellationToken);
            return;
        }

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO chat_emoji_tone_preferences (user_id, default_emoji, preferred_emoji)
                VALUES ({userId}, {defaultEmoji}, {preferredEmoji})
                ON CONFLICT (user_id, default_emoji)
                DO UPDATE SET preferred_emoji = EXCLUDED.preferred_emoji
                """, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == "22P05")
        {
            throw new InvalidOperationException(UnsupportedDatabaseEncodingMessage, exception);
        }
    }

    public async Task<IReadOnlyList<ChatUser>> GetActiveUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireUserAsync(db, cancellationToken);
        return await db.Users.AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.DisplayName).Select(x => new ChatUser(x.Id, x.DisplayName))
            .ToListAsync(cancellationToken);
    }

    public async Task<long> OpenDirectAsync(string otherUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (!await db.Users.AnyAsync(x => x.Id == otherUserId && x.IsActive, cancellationToken))
            throw new InvalidOperationException("Select an active user.");

        var pairKey = string.CompareOrdinal(userId, otherUserId) < 0
            ? $"{userId}|{otherUserId}" : $"{otherUserId}|{userId}";
        var existing = await db.ChatConversations.AsNoTracking()
            .Where(x => x.DirectPairKey == pairKey).Select(x => x.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing != 0) return existing;

        var now = clock.GetUtcNow().UtcDateTime;
        var conversation = new Conversation
        {
            Kind = ConversationKind.Direct, DirectPairKey = pairKey,
            CreatedByUserId = userId, CreatedAtUtc = now, LastActivityAtUtc = now,
            Members = new[] { userId, otherUserId }.Distinct()
                .Select(id => new ConversationMember { UserId = id, JoinedAtUtc = now }).ToList()
        };
        db.ChatConversations.Add(conversation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await using var retryDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await retryDb.ChatConversations.AsNoTracking()
                .Where(x => x.DirectPairKey == pairKey).Select(x => x.Id)
                .SingleAsync(cancellationToken);
        }
        notifications.Publish([userId, otherUserId]);
        return conversation.Id;
    }

    public async Task<long> CreateChannelAsync(string name, ChannelVisibility visibility,
        IEnumerable<string> initialUserIds, long? groupId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        name = name.Trim();
        if (name.Length is < 1 or > 120) throw new InvalidOperationException("Channel name must be 1 to 120 characters.");
        if (!Enum.IsDefined(visibility)) throw new InvalidOperationException("Choose a channel visibility.");
        string[] ids;
        if (visibility == ChannelVisibility.Public)
            ids = await db.Users.Where(x => x.IsActive).Select(x => x.Id).ToArrayAsync(cancellationToken);
        else
        {
            ids = initialUserIds.Where(x => !string.IsNullOrWhiteSpace(x)).Append(userId)
                .Distinct(StringComparer.Ordinal).ToArray();
            var activeCount = await db.Users.CountAsync(x => ids.Contains(x.Id) && x.IsActive, cancellationToken);
            if (activeCount != ids.Length) throw new InvalidOperationException("All channel members must be active users.");
        }
        var now = clock.GetUtcNow().UtcDateTime;
        await using var layoutTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        if (groupId is long destinationId)
        {
            var access = await LoadGroupAccessAsync(db, userId, cancellationToken);
            if (!access.TryGetValue(destinationId, out var destination) || !destination.Visible)
                throw new UnauthorizedAccessException("You cannot create a channel in that folder.");
        }
        var sortOrder = await NextSiblingOrderAsync(db, groupId, cancellationToken);
        var conversation = new Conversation
        {
            Kind = ConversationKind.Channel, Name = name, Visibility = visibility,
            ChannelGroupId = groupId, ChannelSortOrder = sortOrder,
            CreatedByUserId = userId, CreatedAtUtc = now, LastActivityAtUtc = now,
            Members = ids.Select(id => new ConversationMember
            {
                UserId = id, JoinedAtUtc = now, IsOwner = id == userId
            }).ToList()
        };
        db.ChatConversations.Add(conversation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsCharacterEncodingFailure(exception))
        {
            throw new InvalidOperationException(UnsupportedDatabaseEncodingMessage, exception);
        }
        await layoutTransaction.CommitAsync(cancellationToken);
        notifications.Publish(ids);
        return conversation.Id;
    }

    public async Task<IReadOnlyList<ChatConversationRow>> GetConversationsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await db.ChatConversationMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.Conversation.LastActivityAtUtc)
            .ThenByDescending(m => m.ConversationId)
            .Select(m => new ChatConversationRow(
                m.ConversationId, m.Conversation.Kind, m.Conversation.Visibility,
                m.Conversation.Kind == ConversationKind.Direct
                    ? m.Conversation.Members.OrderBy(x => x.UserId == userId)
                        .Select(x => x.User.DisplayName).FirstOrDefault() ?? "Direct Message"
                    : m.Conversation.Name!,
                m.Conversation.LastActivityAtUtc,
                Math.Max(m.IsManuallyUnread ? 1 : 0,
                    db.ChatMessages.Count(x => x.ConversationId == m.ConversationId
                        && x.Id > (m.LastReadMessageId ?? 0) && x.SenderUserId != userId
                        && x.DeletedAtUtc == null
                        && (x.ChannelThreadId == null || x.Type == ChatMessageType.ThreadNotice))),
                db.ChatMessageMentions.Any(x => x.UserId == userId && x.Message.ConversationId == m.ConversationId
                    && x.Message.Id > (m.LastReadMessageId ?? 0) && x.Message.SenderUserId != userId
                    && x.Message.DeletedAtUtc == null && x.Message.ChannelThreadId == null)
                || db.ChatMessages.Any(x => x.ConversationId == m.ConversationId
                    && x.Id > (m.LastReadMessageId ?? 0) && x.SenderUserId != userId
                    && x.DeletedAtUtc == null && x.ChannelThreadId == null
                    && x.Type == ChatMessageType.Text && x.ReplyToMessage != null
                    && x.ReplyToMessage.SenderUserId == userId),
                m.IsOwner, m.Conversation.ChannelGroupId, m.Conversation.ChannelSortOrder,
                m.Conversation.Kind == ConversationKind.Direct
                    ? m.Conversation.Members.OrderBy(x => x.UserId == userId)
                        .Select(x => x.UserId).FirstOrDefault()
                    : null,
                m.IsManuallyUnread))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatChannelGroupRow>> GetChannelGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var access = await LoadGroupAccessAsync(db, userId, cancellationToken);
        var rows = await db.ChatChannelGroups.AsNoTracking()
            .OrderBy(group => group.SortOrder).ThenBy(group => group.Id)
            .Select(group => new { group.Id, group.Name, group.ParentGroupId, group.SortOrder })
            .ToListAsync(cancellationToken);
        return rows.Where(group => access[group.Id].Visible)
            .Select(group => new ChatChannelGroupRow(group.Id, group.Name, group.ParentGroupId,
                group.SortOrder, access[group.Id].Manageable)).ToArray();
    }

    public async Task<long> CreateChannelGroupAsync(string name, long? channelId = null, long? parentGroupId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        name = ValidateGroupName(name);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        var access = await LoadGroupAccessAsync(db, userId, cancellationToken);
        if (parentGroupId is long parentId && (!access.TryGetValue(parentId, out var parentAccess) || !parentAccess.Visible))
            throw new UnauthorizedAccessException("You cannot create a folder there.");
        var visibleIds = access.Where(x => x.Value.Visible).Select(x => x.Key).ToArray();
        if (await db.ChatChannelGroups.AnyAsync(x => visibleIds.Contains(x.Id) &&
            x.Name.ToUpper() == name.ToUpper(), cancellationToken))
            throw new InvalidOperationException("A channel group with that name already exists.");
        var group = new ChatChannelGroup
        {
            Name = name,
            ParentGroupId = parentGroupId,
            SortOrder = await NextSiblingOrderAsync(db, parentGroupId, cancellationToken),
            CreatedByUserId = userId,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };
        db.ChatChannelGroups.Add(group);
        if (channelId is long id)
        {
            var channel = await RequireChannelMemberAsync(db, id, userId, cancellationToken);
            channel.ChannelGroup = group;
            channel.ChannelSortOrder = 0;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishLayoutChangedAsync(db, cancellationToken);
        return group.Id;
    }

    public async Task RenameChannelGroupAsync(long groupId, string name,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        name = ValidateGroupName(name);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        var group = await RequireManageableGroupAsync(db, groupId, userId, cancellationToken);
        var visibleIds = (await LoadGroupAccessAsync(db, userId, cancellationToken))
            .Where(x => x.Value.Visible).Select(x => x.Key).ToArray();
        if (await db.ChatChannelGroups.AnyAsync(x => x.Id != groupId && x.Name.ToUpper() == name.ToUpper() &&
            visibleIds.Contains(x.Id), cancellationToken))
            throw new InvalidOperationException("A channel group with that name already exists.");
        group.Name = name;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishLayoutChangedAsync(db, cancellationToken);
    }

    public async Task DeleteChannelGroupAsync(long groupId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        var group = await RequireManageableGroupAsync(db, groupId, userId, cancellationToken);
        var nextOrder = await NextSiblingOrderAsync(db, group.ParentGroupId, cancellationToken);
        var children = await db.ChatChannelGroups.Where(x => x.ParentGroupId == groupId).ToListAsync(cancellationToken);
        var channels = await db.ChatConversations.Where(x => x.ChannelGroupId == groupId).ToListAsync(cancellationToken);
        foreach (var item in OrderedItems(children, channels, groupId))
            item.MoveTo(group.ParentGroupId, nextOrder++);
        db.ChatChannelGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishLayoutChangedAsync(db, cancellationToken);
    }

    public Task MoveChannelAsync(long channelId, long? targetGroupId, long? beforeChannelId,
        CancellationToken cancellationToken = default) =>
        MoveLayoutItemAsync(false, channelId, targetGroupId, beforeChannelId is not null ? false : null,
            beforeChannelId, cancellationToken);

    public Task MoveChannelGroupAsync(long groupId, long? beforeGroupId,
        CancellationToken cancellationToken = default) =>
        MoveLayoutItemAsync(true, groupId, null, beforeGroupId is not null ? true : null,
            beforeGroupId, cancellationToken);

    public async Task MoveLayoutItemAsync(bool isFolder, long itemId, long? targetParentId,
        bool? beforeIsFolder, long? beforeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        var access = await LoadGroupAccessAsync(db, userId, cancellationToken);
        if (targetParentId is long parentId && (!access.TryGetValue(parentId, out var parentAccess) || !parentAccess.Visible))
            throw new UnauthorizedAccessException("You cannot move an item into that folder.");

        if (isFolder)
        {
            if (!access.TryGetValue(itemId, out var sourceAccess) || !sourceAccess.Manageable)
                throw new UnauthorizedAccessException("You cannot move that folder.");
        }
        else
            await RequireChannelMemberAsync(db, itemId, userId, cancellationToken);

        if (beforeId is not null && beforeIsFolder is null)
            throw new InvalidOperationException("Choose a valid drop target.");
        if (beforeId == itemId && beforeIsFolder == isFolder) return;
        if (beforeId is long siblingId)
        {
            if (beforeIsFolder == true)
            {
                if (!access.TryGetValue(siblingId, out var siblingAccess) || !siblingAccess.Visible)
                    throw new UnauthorizedAccessException("You cannot use that folder as a drop target.");
            }
            else
                await RequireChannelMemberAsync(db, siblingId, userId, cancellationToken);
        }

        var groups = await db.ChatChannelGroups.ToListAsync(cancellationToken);
        var channels = await db.ChatConversations.Where(x => x.Kind == ConversationKind.Channel)
            .ToListAsync(cancellationToken);
        var moving = isFolder
            ? new LayoutItem(groups.Single(x => x.Id == itemId), null)
            : new LayoutItem(null, channels.Single(x => x.Id == itemId));
        if (isFolder && targetParentId is long destinationId)
        {
            var parent = destinationId;
            while (true)
            {
                if (parent == itemId) throw new InvalidOperationException("A folder cannot contain itself or its parent.");
                var next = groups.Single(x => x.Id == parent).ParentGroupId;
                if (next is not long nextId) break;
                parent = nextId;
            }
        }

        var sourceParentId = moving.ParentGroupId;
        var source = OrderedItems(groups, channels, sourceParentId);
        var target = sourceParentId == targetParentId ? source : OrderedItems(groups, channels, targetParentId);
        source.RemoveAll(x => x.IsFolder == isFolder && x.Id == itemId);
        var insertAt = beforeId is long targetId
            ? target.FindIndex(x => x.IsFolder == beforeIsFolder && x.Id == targetId) : target.Count;
        if (insertAt < 0) throw new InvalidOperationException("The drop target is no longer in that folder.");
        target.Insert(insertAt, moving);
        for (var index = 0; index < source.Count; index++) source[index].MoveTo(sourceParentId, index);
        for (var index = 0; index < target.Count; index++) target[index].MoveTo(targetParentId, index);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishLayoutChangedAsync(db, cancellationToken);
    }

    private static string ValidateGroupName(string name)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 120) throw new InvalidOperationException("Group name must be 1 to 120 characters.");
        return name;
    }

    private sealed record GroupInfo(long Id, long? ParentGroupId, string CreatedByUserId);
    private sealed record ChannelAccess(long? GroupId, bool IsMember);
    private sealed record GroupAccess(bool Visible, bool Manageable, bool HasChannels);

    private sealed record LayoutItem(ChatChannelGroup? Folder, Conversation? Channel)
    {
        public bool IsFolder => Folder is not null;
        public long Id => Folder?.Id ?? Channel!.Id;
        public long? ParentGroupId => Folder is not null ? Folder.ParentGroupId : Channel!.ChannelGroupId;
        public int SortOrder => Folder?.SortOrder ?? Channel!.ChannelSortOrder;

        public void MoveTo(long? parentGroupId, int sortOrder)
        {
            if (Folder is not null)
            {
                Folder.ParentGroupId = parentGroupId;
                Folder.SortOrder = sortOrder;
            }
            else
            {
                Channel!.ChannelGroupId = parentGroupId;
                Channel.ChannelSortOrder = sortOrder;
            }
        }
    }

    private static List<LayoutItem> OrderedItems(IEnumerable<ChatChannelGroup> groups,
        IEnumerable<Conversation> channels, long? parentGroupId) =>
        groups.Where(x => x.ParentGroupId == parentGroupId).Select(x => new LayoutItem(x, null))
            .Concat(channels.Where(x => x.ChannelGroupId == parentGroupId).Select(x => new LayoutItem(null, x)))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.IsFolder ? 0 : 1).ThenBy(x => x.Id).ToList();

    private static async Task<int> NextSiblingOrderAsync(AppDbContext db, long? parentGroupId,
        CancellationToken cancellationToken)
    {
        var folderOrder = await db.ChatChannelGroups.Where(x => x.ParentGroupId == parentGroupId)
            .MaxAsync(x => (int?)x.SortOrder, cancellationToken) ?? -1;
        var channelOrder = await db.ChatConversations
            .Where(x => x.Kind == ConversationKind.Channel && x.ChannelGroupId == parentGroupId)
            .MaxAsync(x => (int?)x.ChannelSortOrder, cancellationToken) ?? -1;
        return Math.Max(folderOrder, channelOrder) + 1;
    }

    private static async Task<Dictionary<long, GroupAccess>> LoadGroupAccessAsync(AppDbContext db, string userId,
        CancellationToken cancellationToken)
    {
        var groups = await db.ChatChannelGroups.AsNoTracking()
            .Select(x => new GroupInfo(x.Id, x.ParentGroupId, x.CreatedByUserId))
            .ToListAsync(cancellationToken);
        var channels = await db.ChatConversations.AsNoTracking()
            .Where(x => x.Kind == ConversationKind.Channel && x.ChannelGroupId != null)
            .Select(x => new ChannelAccess(x.ChannelGroupId,
                x.Members.Any(member => member.UserId == userId)))
            .ToListAsync(cancellationToken);
        var childrenByParent = groups.ToLookup(x => x.ParentGroupId);
        var channelsByParent = channels.ToLookup(x => x.GroupId);
        var access = new Dictionary<long, GroupAccess>();
        var visiting = new HashSet<long>();

        GroupAccess Resolve(GroupInfo group)
        {
            if (access.TryGetValue(group.Id, out var cached)) return cached;
            if (!visiting.Add(group.Id)) throw new InvalidOperationException("The channel folder hierarchy contains a cycle.");
            var direct = channelsByParent[group.Id].ToArray();
            var children = childrenByParent[group.Id].ToArray();
            var childAccess = children.Select(Resolve).ToArray();
            var hasChannels = direct.Length > 0 || childAccess.Any(x => x.HasChannels);
            var visible = direct.Any(x => x.IsMember) || childAccess.Any(x => x.Visible)
                || (!hasChannels && group.CreatedByUserId == userId);
            var manageable = direct.All(x => x.IsMember) && childAccess.All(x => x.Manageable)
                && (hasChannels || group.CreatedByUserId == userId);
            visiting.Remove(group.Id);
            return access[group.Id] = new GroupAccess(visible, manageable, hasChannels);
        }

        foreach (var group in groups) Resolve(group);
        return access;
    }

    private static async Task<Conversation> RequireChannelMemberAsync(AppDbContext db, long channelId,
        string userId, CancellationToken cancellationToken) =>
        await db.ChatConversations.SingleOrDefaultAsync(x => x.Id == channelId && x.Kind == ConversationKind.Channel
            && x.Members.Any(member => member.UserId == userId), cancellationToken)
        ?? throw new UnauthorizedAccessException("You do not belong to that channel.");

    private static async Task<ChatChannelGroup> RequireManageableGroupAsync(AppDbContext db, long groupId,
        string userId, CancellationToken cancellationToken)
    {
        var access = await LoadGroupAccessAsync(db, userId, cancellationToken);
        if (!access.TryGetValue(groupId, out var groupAccess) || !groupAccess.Manageable)
            throw new UnauthorizedAccessException("You must belong to every channel in the group to manage it.");
        return await db.ChatChannelGroups.SingleAsync(x => x.Id == groupId, cancellationToken);
    }

    private static Task LockChannelLayoutAsync(AppDbContext db, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(2047792034)", cancellationToken);

    private async Task PublishLayoutChangedAsync(AppDbContext db, CancellationToken cancellationToken) =>
        notifications.Publish(await db.Users.AsNoTracking().Where(x => x.IsActive)
            .Select(x => x.Id).ToArrayAsync(cancellationToken));

    public async Task<int> GetTotalUnreadCountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var total = await db.ChatConversationMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(db.ChatMessages.AsNoTracking().Where(x => x.SenderUserId != userId
                    && x.DeletedAtUtc == null
                    && (x.ChannelThreadId == null || x.Type == ChatMessageType.ThreadNotice)),
                member => member.ConversationId,
                message => message.ConversationId,
                (member, message) => new { message.Id, member.LastReadMessageId })
            .LongCountAsync(x => x.Id > (x.LastReadMessageId ?? 0), cancellationToken);
        var manualOnly = await db.ChatConversationMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.IsManuallyUnread)
            .CountAsync(m => !db.ChatMessages.Any(message => message.ConversationId == m.ConversationId
                && message.Id > (m.LastReadMessageId ?? 0) && message.SenderUserId != userId
                && message.DeletedAtUtc == null
                && (message.ChannelThreadId == null || message.Type == ChatMessageType.ThreadNotice)),
                cancellationToken);
        return (int)Math.Min(total + manualOnly, int.MaxValue);
    }

    public async Task<bool> HasUnreadMentionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await db.ChatConversationMembers.AsNoTracking().Where(x => x.UserId == userId)
            .AnyAsync(member => db.ChatMessageMentions.Any(mention => mention.UserId == userId
                && mention.Message.ConversationId == member.ConversationId
                && mention.Message.Id > (member.LastReadMessageId ?? 0)
                && mention.Message.SenderUserId != userId
                && mention.Message.DeletedAtUtc == null
                && mention.Message.ChannelThreadId == null), cancellationToken);
    }

    public async Task<bool> HasUnreadAlertAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await db.ChatConversationMembers.AsNoTracking().Where(member => member.UserId == userId)
            .AnyAsync(member =>
                (member.Conversation.Kind == ConversationKind.Direct &&
                    (member.IsManuallyUnread || db.ChatMessages.Any(message =>
                        message.ConversationId == member.ConversationId
                        && message.Id > (member.LastReadMessageId ?? 0)
                        && message.SenderUserId != userId && message.DeletedAtUtc == null)))
                || db.ChatMessageMentions.Any(mention => mention.UserId == userId
                    && mention.Message.ConversationId == member.ConversationId
                    && mention.Message.Id > (member.LastReadMessageId ?? 0)
                    && mention.Message.SenderUserId != userId
                    && mention.Message.DeletedAtUtc == null
                    && mention.Message.ChannelThreadId == null)
                || db.ChatMessages.Any(message => message.ConversationId == member.ConversationId
                    && message.Id > (member.LastReadMessageId ?? 0)
                    && message.SenderUserId != userId && message.DeletedAtUtc == null
                    && message.ChannelThreadId == null
                    && message.Type == ChatMessageType.Text
                    && message.ReplyToMessage != null
                    && message.ReplyToMessage.SenderUserId == userId), cancellationToken);
    }

    public async Task AddPrivateMemberAsync(long conversationId, string memberUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await RequirePrivateOwnerAsync(db, conversationId, userId, cancellationToken);
        if (!await db.Users.AnyAsync(x => x.Id == memberUserId && x.IsActive, cancellationToken))
            throw new InvalidOperationException("Select an active user.");
        if (await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId && x.UserId == memberUserId, cancellationToken)) return;
        var latest = await db.ChatMessages.Where(x => x.ConversationId == conversationId)
            .MaxAsync(x => (long?)x.Id, cancellationToken);
        db.ChatConversationMembers.Add(new ConversationMember
        {
            ConversationId = conversationId, UserId = memberUserId,
            JoinedAtUtc = clock.GetUtcNow().UtcDateTime, LastReadMessageId = latest
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { }
        notifications.Publish([memberUserId, userId]);
    }

    public async Task RenameChannelAsync(long conversationId, string name, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        name = name.Trim();
        if (name.Length is < 1 or > 120)
            throw new InvalidOperationException("Channel name must be 1 to 120 characters.");
        var administrator = await IsAdministratorAsync(db, userId, cancellationToken);
        var channel = await db.ChatConversations.SingleOrDefaultAsync(x => x.Id == conversationId
            && x.Kind == ConversationKind.Channel && (administrator ||
                x.Members.Any(member => member.UserId == userId && member.IsOwner)),
            cancellationToken);
        if (channel is null) throw new UnauthorizedAccessException("You cannot rename this channel.");
        if (channel.Name == name) return;
        channel.Name = name;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsCharacterEncodingFailure(exception))
        {
            throw new InvalidOperationException(UnsupportedDatabaseEncodingMessage, exception);
        }
        var memberIds = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == conversationId).Select(x => x.UserId).ToArrayAsync(cancellationToken);
        notifications.Publish(memberIds);
    }

    public async Task<IReadOnlyList<ChatUser>> GetChannelMembersAsync(long conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId
            && x.UserId == userId && x.Conversation.Kind == ConversationKind.Channel, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this channel.");
        return await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .OrderBy(x => x.User.DisplayName)
            .Select(x => new ChatUser(x.UserId, x.User.DisplayName, x.User.UserName))
            .ToListAsync(cancellationToken);
    }

    public async Task RemovePrivateMemberAsync(long conversationId, string memberUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await RequirePrivateOwnerAsync(db, conversationId, userId, cancellationToken);
        var member = await db.ChatConversationMembers.SingleOrDefaultAsync(
            x => x.ConversationId == conversationId && x.UserId == memberUserId, cancellationToken);
        if (member is null) return;
        if (member.IsOwner) throw new InvalidOperationException("The channel owner cannot be removed.");
        db.ChatConversationMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        notifications.Publish([memberUserId, userId]);
    }

    private static async Task RequirePrivateOwnerAsync(AppDbContext db, long conversationId,
        string userId, CancellationToken cancellationToken)
    {
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId
            && x.UserId == userId && x.IsOwner && x.Conversation.Kind == ConversationKind.Channel
            && x.Conversation.Visibility == ChannelVisibility.Private, cancellationToken))
            throw new UnauthorizedAccessException("Only the private channel owner can manage members.");
    }

    public async Task<long> CreateChannelThreadAsync(long conversationId, string title, string initialMessage,
        CancellationToken cancellationToken = default, long? startedFromMessageId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        title = title.Trim();
        if (title.Length is < 1 or > 120)
            throw new InvalidOperationException("Thread title must be 1 to 120 characters.");
        initialMessage = initialMessage.Trim();
        if (initialMessage.Length is < 1 or > 4000)
            throw new InvalidOperationException("The first thread message must be 1 to 4000 characters.");
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId
            && x.UserId == userId && x.Conversation.Kind == ConversationKind.Channel, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this channel.");
        if (startedFromMessageId is long sourceId && !await db.ChatMessages.AnyAsync(x =>
                x.Id == sourceId && x.ConversationId == conversationId && x.ChannelThreadId == null
                && x.Type == ChatMessageType.Text && x.ReplyToMessageId != null
                && x.DeletedAtUtc == null, cancellationToken))
            throw new InvalidOperationException("Choose an existing channel reply to start this thread.");

        var now = clock.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var channelThread = new ChatChannelThread
        {
            ConversationId = conversationId, Title = title, CreatedByUserId = userId,
            CreatedAtUtc = now, LastMessageAtUtc = now, StartedFromMessageId = startedFromMessageId
        };
        db.ChatChannelThreads.Add(channelThread);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsCharacterEncodingFailure(exception))
        {
            throw new InvalidOperationException(UnsupportedDatabaseEncodingMessage, exception);
        }
        var mentions = await ResolveMentionsAsync(db, conversationId, userId, initialMessage, now, cancellationToken);
        if (mentions.Body.Length is < 1 or > 4000)
            throw new InvalidOperationException("The first thread message must be 1 to 4000 characters.");
        db.ChatMessages.AddRange(new ChatMessage
        {
            ConversationId = conversationId, ChannelThreadId = channelThread.Id,
            SenderUserId = userId, Type = ChatMessageType.ThreadNotice,
            Body = "started a thread", SentAtUtc = now
        }, new ChatMessage
        {
            ConversationId = conversationId, ChannelThreadId = channelThread.Id,
            SenderUserId = userId, Type = ChatMessageType.Text,
            Body = mentions.Body, SentAtUtc = now,
            Mentions = mentions.Recipients.Select(id => new ChatMessageMention { UserId = id }).ToList(),
            Tags = mentions.Tags.Select(token => new ChatMessageTag
            {
                Start = token.Start, Length = token.Length, Kind = token.Kind, UserId = token.UserId
            }).ToList()
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsCharacterEncodingFailure(exception))
        {
            throw new InvalidOperationException(UnsupportedDatabaseEncodingMessage, exception);
        }
        await db.ChatConversations.Where(x => x.Id == conversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastActivityAtUtc,
                x => x.LastActivityAtUtc > now ? x.LastActivityAtUtc : now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var recipients = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == conversationId).Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        notifications.Publish(recipients);
        return channelThread.Id;
    }

    public async Task<IReadOnlyList<ChatChannelThreadRow>> GetChannelThreadsAsync(long conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId
            && x.UserId == userId && x.Conversation.Kind == ConversationKind.Channel, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this channel.");
        return await db.ChatChannelThreads.AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.LastMessageAtUtc).ThenByDescending(x => x.Id)
            .Select(x => new ChatChannelThreadRow(x.Id, x.ConversationId, x.Title,
                x.CreatedByUser.DisplayName, x.CreatedAtUtc, x.LastMessageAtUtc,
                db.ChatMessages.Count(message => message.ChannelThreadId == x.Id
                    && (message.Type == ChatMessageType.Text || message.Type == ChatMessageType.Poll) && message.DeletedAtUtc == null),
                db.ChatMessages.Where(message => message.ChannelThreadId == x.Id
                        && (message.Type == ChatMessageType.Text || message.Type == ChatMessageType.Poll) && message.DeletedAtUtc == null)
                    .OrderByDescending(message => message.Id).Select(message => message.Body)
                    .FirstOrDefault(), x.StartedFromMessageId,
                x.StartedFromMessage == null || x.StartedFromMessage.DeletedAtUtc != null
                    ? null : x.StartedFromMessage.Body))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatChannelThreadRow>> GetActiveChannelThreadsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var cutoff = clock.GetUtcNow().UtcDateTime - ChannelThreadActiveFor;
        return await db.ChatChannelThreads.AsNoTracking()
            .Where(x => x.LastMessageAtUtc >= cutoff && db.ChatConversationMembers.Any(member =>
                member.ConversationId == x.ConversationId && member.UserId == userId))
            .OrderByDescending(x => x.LastMessageAtUtc).ThenByDescending(x => x.Id)
            .Select(x => new ChatChannelThreadRow(x.Id, x.ConversationId, x.Title,
                x.CreatedByUser.DisplayName, x.CreatedAtUtc, x.LastMessageAtUtc,
                db.ChatMessages.Count(message => message.ChannelThreadId == x.Id
                    && (message.Type == ChatMessageType.Text || message.Type == ChatMessageType.Poll) && message.DeletedAtUtc == null),
                db.ChatMessages.Where(message => message.ChannelThreadId == x.Id
                        && (message.Type == ChatMessageType.Text || message.Type == ChatMessageType.Poll) && message.DeletedAtUtc == null)
                    .OrderByDescending(message => message.Id).Select(message => message.Body)
                    .FirstOrDefault(), x.StartedFromMessageId,
                x.StartedFromMessage == null || x.StartedFromMessage.DeletedAtUtc != null
                    ? null : x.StartedFromMessage.Body))
            .ToListAsync(cancellationToken);
    }

    public async Task<ChatThread> GetThreadAsync(long conversationId, CancellationToken cancellationToken = default,
        long? throughMessageId = null, long? channelThreadId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this conversation.");
        var conversation = (await GetConversationsAsync(cancellationToken)).Single(x => x.Id == conversationId);
        ChatChannelThreadRow? channelThread = null;
        if (channelThreadId is long requestedThreadId)
        {
            if (conversation.Kind != ConversationKind.Channel)
                throw new InvalidOperationException("Direct conversations do not have threads.");
            channelThread = (await GetChannelThreadsAsync(conversationId, cancellationToken))
                .SingleOrDefault(x => x.Id == requestedThreadId)
                ?? throw new InvalidOperationException("That thread does not belong to this channel.");
        }
        if (throughMessageId is long targetId && !await db.ChatMessages.AnyAsync(
                x => x.ConversationId == conversationId && x.ChannelThreadId == channelThreadId
                    && x.Id == targetId && x.DeletedAtUtc == null, cancellationToken))
            throw new InvalidOperationException("The original message is no longer available.");
        var messages = await db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == conversationId
                && x.DeletedAtUtc == null
                && (channelThreadId == null
                    ? x.ChannelThreadId == null || x.Type == ChatMessageType.ThreadNotice
                    : x.ChannelThreadId == channelThreadId && x.Type != ChatMessageType.ThreadNotice)
                && (throughMessageId == null || x.Id <= throughMessageId))
            .OrderByDescending(x => x.Id).Take(100).OrderBy(x => x.Id)
            .Select(x => new ChatMessageRow(x.Id, x.Type, x.SenderUserId,
                x.SenderUser == null ? "System" : x.SenderUser.DisplayName,
                x.DeletedAtUtc == null ? x.Body : null, x.SentAtUtc, x.EditedAtUtc, x.DeletedAtUtc != null, x.PinnedAtUtc,
                Array.Empty<ChatReactionRow>(), Array.Empty<ChatMessagePart>(),
                x.ReplyToMessageId,
                x.ReplyToMessage == null ? null : x.ReplyToMessage.SenderUserId,
                x.ReplyToMessage == null ? null : x.ReplyToMessage.SenderUser == null
                    ? "System" : x.ReplyToMessage.SenderUser.DisplayName,
                x.ReplyToMessage == null ? null : x.ReplyToMessage.DeletedAtUtc == null
                    ? x.ReplyToMessage.Body : null, Array.Empty<ChatAttachmentRow>(), x.ChannelThreadId, null))
            .ToListAsync(cancellationToken);
        var messageIds = messages.Select(x => x.Id).ToArray();
        var polls = await ReadPollsAsync(db, messageIds, userId, cancellationToken);
        var attachmentRows = await db.ChatAttachments.AsNoTracking()
            .Where(x => messageIds.Contains(x.MessageId))
            .Select(x => new { x.Id, x.MessageId, x.FileName, x.Kind,
                Size = x.Content.Length,
                TextContent = x.Kind == ChatAttachmentKind.Text ? x.Content : null })
            .ToListAsync(cancellationToken);
        var attachmentsById = attachmentRows.ToDictionary(x => x.Id,
            x => new ChatAttachmentRow(x.Id, x.FileName, x.Kind, x.Size,
                x.TextContent is null ? null : ChatAttachmentTypes.DecodeText(x.TextContent)));
        var olderTextIds = attachmentRows
            .Where(x => x.Kind == ChatAttachmentKind.Download && x.Size <= ChatAttachmentTypes.MaximumTextBytes
                && ChatAttachmentTypes.IsSupportedTextFileName(x.FileName))
            .Select(x => x.Id).ToArray();
        if (olderTextIds.Length > 0)
        {
            // Source files uploaded before their extensions were supported remain tagged as downloads.
            var olderTextFiles = await db.ChatAttachments.AsNoTracking()
                .Where(x => olderTextIds.Contains(x.Id))
                .Select(x => new { x.Id, x.FileName, x.Content })
                .ToListAsync(cancellationToken);
            foreach (var file in olderTextFiles)
            {
                if (ChatAttachmentTypes.Classify(file.FileName, file.Content).Kind != ChatAttachmentKind.Text) continue;
                attachmentsById[file.Id] = new ChatAttachmentRow(file.Id, file.FileName,
                    ChatAttachmentKind.Text, file.Content.Length, ChatAttachmentTypes.DecodeText(file.Content));
            }
        }
        var attachmentsByMessage = attachmentRows.GroupBy(x => x.MessageId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ChatAttachmentRow>)group
                .Select(x => attachmentsById[x.Id]).ToArray());
        var reactionRows = await db.ChatMessageReactions.AsNoTracking()
            .Where(x => messageIds.Contains(x.MessageId))
            .OrderBy(x => x.ReactedAtUtc).ThenBy(x => x.User.DisplayName)
            .Select(x => new { x.MessageId, x.Emoji, x.UserId, x.User.DisplayName })
            .ToListAsync(cancellationToken);
        var reactionsByMessage = reactionRows.GroupBy(x => x.MessageId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ChatReactionRow>)group
                .GroupBy(x => x.Emoji)
                .Select(emoji => new ChatReactionRow(emoji.Key,
                    emoji.Select(x => new ChatUser(x.UserId, x.DisplayName)).ToArray()))
                .ToArray());
        var members = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .OrderBy(x => x.User.DisplayName)
            .Select(x => new ChatUser(x.UserId, x.User.DisplayName, x.User.UserName))
            .ToListAsync(cancellationToken);
        var tagRows = conversation.Kind == ConversationKind.Channel
            ? await db.ChatMessageTags.AsNoTracking()
                .Where(x => messageIds.Contains(x.MessageId))
                .Select(x => new { x.MessageId, x.Start, x.Length, x.Kind, x.UserId,
                    Name = x.User == null ? null : x.User.DisplayName })
                .ToListAsync(cancellationToken)
            : [];
        var tagsByMessage = tagRows.GroupBy(x => x.MessageId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ChatMentionToken>)group
                .Select(x => new ChatMentionToken(x.Start, x.Length, x.Kind, x.UserId, x.Name))
                .ToArray());
        var mentionRows = conversation.Kind == ConversationKind.Channel
            ? await db.ChatMessageMentions.AsNoTracking()
                .Where(x => messageIds.Contains(x.MessageId))
                .Select(x => new { x.MessageId, x.UserId, x.User.DisplayName, x.User.UserName })
                .ToListAsync(cancellationToken)
            : [];
        var mentionsByMessage = mentionRows.GroupBy(x => x.MessageId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        messages = messages.Select(x =>
        {
            var tags = tagsByMessage.GetValueOrDefault(x.Id);
            if (tags is null && x.Body is not null && mentionsByMessage.TryGetValue(x.Id, out var legacyMentions))
            {
                // Messages sent before tag positions were saved still have their recipient rows.
                var knownUsers = members.Concat(legacyMentions.Select(mention =>
                    new ChatUser(mention.UserId, mention.DisplayName, mention.UserName)))
                    .DistinctBy(member => member.Id).ToArray();
                var recipientIds = legacyMentions.Select(mention => mention.UserId)
                    .ToHashSet(StringComparer.Ordinal);
                tags = ChatMentionParser.FindTokens(x.Body, knownUsers)
                    .Where(token => token.Kind != ChatMentionKind.User || recipientIds.Contains(token.UserId!))
                    .ToArray();
            }
            return x with
            {
                Reactions = reactionsByMessage.GetValueOrDefault(x.Id) ?? [],
                Poll = polls.GetValueOrDefault(x.Id),
                Attachments = attachmentsByMessage.GetValueOrDefault(x.Id) ?? [],
                Parts = x.Body is null ? [] : conversation.Kind == ConversationKind.Channel
                    ? ChatMentionParser.GetParts(x.Body, tags ?? [])
                    : [new ChatMessagePart(x.Body, false)]
            };
        }).ToList();
        var pinnedMessages = await db.ChatMessages.AsNoTracking()
            .Where(x => x.ConversationId == conversationId && x.ChannelThreadId == channelThreadId
                && x.PinnedAtUtc != null && x.DeletedAtUtc == null)
            .OrderByDescending(x => x.PinnedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => new ChatPinnedMessageRow(x.Id,
                x.SenderUser == null ? "System" : x.SenderUser.DisplayName, x.Body, x.SentAtUtc))
            .ToListAsync(cancellationToken);
        return new ChatThread(conversation, messages, members, pinnedMessages, channelThread);
    }

    public async Task<long> SendAsync(long conversationId, string body, CancellationToken cancellationToken = default,
        long? replyToMessageId = null, string? attachmentFileName = null, byte[]? attachmentContent = null,
        long? channelThreadId = null)
    {
        if ((attachmentFileName is null) != (attachmentContent is null))
            throw new InvalidOperationException("Choose a file before sending it.");
        var attachments = attachmentFileName is null
            ? Array.Empty<ChatAttachmentUpload>()
            : [new ChatAttachmentUpload(attachmentFileName, attachmentContent!)];
        return await SendWithAttachmentsAsync(conversationId, body, attachments, cancellationToken,
            replyToMessageId, channelThreadId);
    }

    public async Task<long> SendWithAttachmentsAsync(long conversationId, string body,
        IReadOnlyList<ChatAttachmentUpload> attachments, CancellationToken cancellationToken = default,
        long? replyToMessageId = null, long? channelThreadId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        body = body.Trim();
        if (attachments.Count > ChatAttachmentTypes.MaximumFilesPerMessage)
            throw new InvalidOperationException($"A message can include at most {ChatAttachmentTypes.MaximumFilesPerMessage} files.");
        var totalAttachmentBytes = 0L;
        var messageAttachments = new List<ChatAttachment>(attachments.Count);
        foreach (var upload in attachments)
        {
            if (upload.Content.Length is < 1 or > ChatAttachmentTypes.MaximumBytes)
                throw new InvalidOperationException("Each file must be between 1 byte and 25 MB.");
            totalAttachmentBytes += upload.Content.Length;
            if (totalAttachmentBytes > ChatAttachmentTypes.MaximumMessageBytes)
                throw new InvalidOperationException("Attachments in a message cannot total more than 25 MB.");
            var safeName = upload.FileName.Replace('\\', '/').Split('/').Last();
            if (string.IsNullOrWhiteSpace(safeName) || safeName.Length > 255 || safeName.Any(char.IsControl))
                throw new InvalidOperationException("The file name is invalid or exceeds 255 characters.");
            var (kind, contentType) = ChatAttachmentTypes.Classify(safeName, upload.Content);
            messageAttachments.Add(new ChatAttachment
            {
                FileName = safeName, Content = upload.Content, Kind = kind, ContentType = contentType
            });
        }
        if (messageAttachments.Count > 0 && body.Length == 0) body = "Shared a file";
        if (body.Length is < 1 or > 4000) throw new InvalidOperationException("Message must be 1 to 4000 characters.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("Join this conversation before sending a message.");
        if (channelThreadId is long requestedThreadId && !await db.ChatChannelThreads.AnyAsync(x =>
                x.Id == requestedThreadId && x.ConversationId == conversationId
                && x.Conversation.Kind == ConversationKind.Channel, cancellationToken))
            throw new InvalidOperationException("That thread does not belong to this channel.");
        if (replyToMessageId is long targetId && !await db.ChatMessages.AnyAsync(x =>
                x.Id == targetId && x.ConversationId == conversationId
                    && x.ChannelThreadId == channelThreadId && x.Type == ChatMessageType.Text
                    && x.DeletedAtUtc == null,
                cancellationToken))
            throw new InvalidOperationException("Reply to an existing message in this conversation.");
        var now = clock.GetUtcNow().UtcDateTime;
        var mentions = await ResolveMentionsAsync(db, conversationId, userId, body, now, cancellationToken);
        if (mentions.Body.Length > 4000) throw new InvalidOperationException("Message must be 1 to 4000 characters.");
        var message = new ChatMessage
        {
            ConversationId = conversationId, ChannelThreadId = channelThreadId,
            SenderUserId = userId, Body = mentions.Body,
            ReplyToMessageId = replyToMessageId,
            SentAtUtc = now, Type = ChatMessageType.Text,
            Attachments = messageAttachments,
            Mentions = mentions.Recipients.Select(id => new ChatMessageMention { UserId = id }).ToList(),
            Tags = mentions.Tags.Select(token => new ChatMessageTag
            {
                Start = token.Start, Length = token.Length, Kind = token.Kind, UserId = token.UserId
            }).ToList()
        };
        db.ChatMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsCharacterEncodingFailure(exception))
        {
            throw new InvalidOperationException(UnsupportedDatabaseEncodingMessage, exception);
        }
        // Sending in a manually unread conversation is an explicit read action. Keep it
        // in the send transaction so every caller has the same behavior.
        await db.ChatConversationMembers.Where(x => x.ConversationId == conversationId
                && x.UserId == userId && x.IsManuallyUnread)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastReadMessageId, (long?)message.Id)
                .SetProperty(x => x.IsManuallyUnread, false), cancellationToken);
        await db.ChatConversations.Where(x => x.Id == conversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastActivityAtUtc,
                x => x.LastActivityAtUtc > now ? x.LastActivityAtUtc : now), cancellationToken);
        if (channelThreadId is long threadId)
            await db.ChatChannelThreads.Where(x => x.Id == threadId && x.ConversationId == conversationId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastMessageAtUtc,
                    x => x.LastMessageAtUtc > now ? x.LastMessageAtUtc : now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var recipients = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == conversationId).Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        notifications.Publish(recipients);
        return message.Id;
    }

    public async Task<ChatAttachmentFile?> GetAttachmentAsync(long attachmentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await FindAttachmentAsync(db, attachmentId, userId, cancellationToken);
    }

    // Ordinary HTTP file requests have an authenticated HttpContext, but no Blazor circuit.
    internal async Task<ChatAttachmentFile?> GetAttachmentForHttpUserAsync(long attachmentId,
        string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken))
            throw new UnauthorizedAccessException("An active account is required for chat.");
        await EnsurePublicChannelMembershipsAsync(db, userId, clock.GetUtcNow().UtcDateTime, cancellationToken);
        return await FindAttachmentAsync(db, attachmentId, userId, cancellationToken);
    }

    private static Task<ChatAttachmentFile?> FindAttachmentAsync(AppDbContext db, long attachmentId,
        string userId, CancellationToken cancellationToken)
    {
        return db.ChatAttachments.AsNoTracking()
            .Where(x => x.Id == attachmentId && x.Message.DeletedAtUtc == null &&
                db.ChatConversationMembers.Any(m => m.ConversationId == x.Message.ConversationId && m.UserId == userId))
            .Select(x => new ChatAttachmentFile(x.FileName, x.ContentType, x.Kind, x.Content))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static async Task<ResolvedMentions> ResolveMentionsAsync(AppDbContext db,
        long conversationId, string senderUserId, string body, DateTime now, CancellationToken cancellationToken)
    {
        if (!await db.ChatConversations.AsNoTracking().AnyAsync(x => x.Id == conversationId
            && x.Kind == ConversationKind.Channel, cancellationToken))
            return new ResolvedMentions(body, new HashSet<string>(), []);

        var members = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == conversationId && x.User.IsActive)
            .Select(x => new ChatUser(x.UserId, x.User.DisplayName, x.User.UserName))
            .ToListAsync(cancellationToken);
        body = ChatMentionParser.NormalizeTags(body, members);
        var tags = ChatMentionParser.FindTokens(body, members);
        var result = tags.Where(x => x.Kind == ChatMentionKind.User)
            .Select(x => x.UserId!).ToHashSet(StringComparer.Ordinal);
        if (tags.Any(x => x.Kind == ChatMentionKind.Everyone)) result.UnionWith(members.Select(x => x.Id));
        if (tags.Any(x => x.Kind == ChatMentionKind.Here))
        {
            var ids = members.Select(x => x.Id).ToArray();
            var preferences = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id))
                .Select(x => new { x.Id, x.PresencePreference })
                .ToListAsync(cancellationToken);
            var activity = await db.UserPresenceSessions.AsNoTracking()
                .Where(x => ids.Contains(x.UserId) && x.LastHeartbeatAtUtc >= now - UserPresenceService.HeartbeatLifetime)
                .GroupBy(x => x.UserId)
                .Select(x => new { UserId = x.Key, LastActivityAtUtc = x.Max(s => s.LastActivityAtUtc) })
                .ToDictionaryAsync(x => x.UserId, x => x.LastActivityAtUtc, cancellationToken);
            foreach (var user in preferences)
            {
                if (UserPresenceService.ResolveState(user.PresencePreference,
                        activity.GetValueOrDefault(user.Id), now, false) is
                    UserPresenceState.Online or UserPresenceState.Idle or UserPresenceState.DoNotDisturb)
                    result.Add(user.Id);
            }
        }
        result.Remove(senderUserId);
        return new ResolvedMentions(body, result, tags);
    }

    public Task AddReactionAsync(long messageId, string emoji, CancellationToken cancellationToken = default) =>
        SaveReactionAsync(messageId, emoji, toggle: false, cancellationToken);

    public Task ToggleReactionAsync(long messageId, string emoji, CancellationToken cancellationToken = default) =>
        SaveReactionAsync(messageId, emoji, toggle: true, cancellationToken);

    private async Task SaveReactionAsync(long messageId, string emoji, bool toggle, CancellationToken cancellationToken)
    {
        emoji = emoji.Normalize(NormalizationForm.FormC);
        if (!IsReactionEmoji(emoji))
            throw new InvalidOperationException("Choose one emoji for the reaction.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Lock the message so two toggles for the same message observe each other in order.
        var messages = await db.ChatMessages.FromSqlInterpolated(
            $"SELECT * FROM chat_messages WHERE id = {messageId} FOR UPDATE").ToListAsync(cancellationToken);
        var message = messages.SingleOrDefault();
        if (message is null || !await db.ChatConversationMembers.AnyAsync(
                x => x.ConversationId == message.ConversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this conversation.");
        if (message.DeletedAtUtc is not null)
            throw new InvalidOperationException("Deleted messages cannot receive reactions.");
        if (message.Type != ChatMessageType.Text)
            throw new InvalidOperationException("Only messages can receive reactions.");

        var existing = await db.ChatMessageReactions.FindAsync([messageId, userId, emoji], cancellationToken);
        if (existing is not null && !toggle) return;
        var added = existing is null;
        if (existing is null)
            db.ChatMessageReactions.Add(new ChatMessageReaction
            {
                MessageId = messageId, UserId = userId, Emoji = emoji,
                ReactedAtUtc = clock.GetUtcNow().UtcDateTime
            });
        else db.ChatMessageReactions.Remove(existing);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (added && !IsThumbReaction(emoji))
                await db.Users.Where(x => x.Id == userId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastReactionEmoji, emoji),
                        cancellationToken);
        }
        catch (DbUpdateException exception) when (IsCharacterEncodingFailure(exception))
        {
            throw new InvalidOperationException(UnsupportedDatabaseEncodingMessage, exception);
        }
        await transaction.CommitAsync(cancellationToken);
        var recipients = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == message.ConversationId).Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        notifications.Publish(recipients);
    }

    private static bool IsThumbReaction(string emoji) =>
        ChatEmojiCatalog.FindFamilyForEmoji(emoji)?.Default.Emoji is "👍" or "👎";

    private static bool IsReactionEmoji(string emoji)
    {
        if (emoji.Length is < 1 or > 32)
            return false;
        if (ChatEmojiCatalog.All.Any(x => x.Emoji == emoji)) return true;
        if (StringInfo.ParseCombiningCharacters(emoji).Length != 1) return false;
        var first = emoji.EnumerateRunes().First().Value;
        return first is >= 0x1F000 and <= 0x1FAFF
            or >= 0x2600 and <= 0x27BF
            or 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139
            or >= 0x2194 and <= 0x21AA
            or >= 0x231A and <= 0x23FA
            or >= 0x25AA and <= 0x25FE
            or >= 0x2934 and <= 0x2935
            or >= 0x3030 and <= 0x3299
            || emoji.EndsWith("\u20E3", StringComparison.Ordinal);
    }

    public async Task MarkReadAsync(long conversationId, long throughMessageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(
            x => x.ConversationId == conversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this conversation.");
        if (!await db.ChatMessages.AnyAsync(x => x.ConversationId == conversationId && x.Id == throughMessageId, cancellationToken))
            throw new InvalidOperationException("Read marker must refer to a message in this conversation.");
        var updated = await db.ChatConversationMembers
            .Where(x => x.ConversationId == conversationId && x.UserId == userId
                && !x.IsManuallyUnread
                && (x.LastReadMessageId == null || x.LastReadMessageId < throughMessageId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastReadMessageId,
                (long?)throughMessageId), cancellationToken);
        if (updated > 0) notifications.Publish([userId]);
    }

    public async Task MarkUnreadAsync(long conversationId, long fromMessageId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId
                && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this conversation.");
        if (!await db.ChatMessages.AnyAsync(x => x.ConversationId == conversationId
                && x.Id == fromMessageId, cancellationToken))
            throw new InvalidOperationException("Unread marker must refer to a message in this conversation.");

        var previousMessageId = await db.ChatMessages.AsNoTracking()
            .Where(x => x.ConversationId == conversationId && x.Id < fromMessageId)
            .MaxAsync(x => (long?)x.Id, cancellationToken);
        var updated = await db.ChatConversationMembers
            .Where(x => x.ConversationId == conversationId && x.UserId == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastReadMessageId, x =>
                    x.LastReadMessageId == null || x.LastReadMessageId < fromMessageId
                        ? x.LastReadMessageId : previousMessageId)
                .SetProperty(x => x.IsManuallyUnread, true), cancellationToken);
        if (updated > 0) notifications.Publish([userId]);
    }

    public async Task<long?> MarkConversationReadAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(
            x => x.ConversationId == conversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this conversation.");

        var latestMessageId = await db.ChatMessages.AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .MaxAsync(x => (long?)x.Id, cancellationToken);
        if (latestMessageId is null) return null;

        var updated = await db.ChatConversationMembers
            .Where(x => x.ConversationId == conversationId && x.UserId == userId
                && (x.IsManuallyUnread || x.LastReadMessageId == null || x.LastReadMessageId < latestMessageId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastReadMessageId, latestMessageId)
                .SetProperty(x => x.IsManuallyUnread, false), cancellationToken);
        if (updated > 0) notifications.Publish([userId]);
        return latestMessageId;
    }

    public async Task DeleteOwnMessageAsync(long messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var message = await db.ChatMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
        if (message is null || message.Type != ChatMessageType.Text || message.SenderUserId != userId
            || !await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == message.ConversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You can only delete your own messages in conversations you belong to.");
        if (message.DeletedAtUtc is not null) return;
        var deleted = await db.ChatMessages.Where(x => x.Id == messageId && x.DeletedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.DeletedAtUtc, clock.GetUtcNow().UtcDateTime)
                .SetProperty(x => x.DeletedByUserId, userId)
                .SetProperty(x => x.PinnedAtUtc, (DateTime?)null)
                .SetProperty(x => x.PinnedByUserId, (string?)null), cancellationToken);
        if (deleted == 0) return;
        var recipients = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == message.ConversationId).Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        notifications.Publish(recipients);
    }

    public async Task SetMessagePinnedAsync(long messageId, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var message = await db.ChatMessages.AsNoTracking()
            .Where(x => x.Id == messageId)
            .Select(x => new { x.ConversationId, x.ChannelThreadId, x.Type, x.DeletedAtUtc, x.PinnedAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (message is null || !await db.ChatConversationMembers.AnyAsync(
                x => x.ConversationId == message.ConversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this conversation.");
        if (message.DeletedAtUtc is not null)
            throw new InvalidOperationException("Deleted messages cannot be pinned.");
        if (message.Type != ChatMessageType.Text)
            throw new InvalidOperationException("Only messages can be pinned.");
        if ((message.PinnedAtUtc is not null) == pinned) return;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var updated = pinned
            ? await db.ChatMessages.Where(x => x.Id == messageId && x.Type == ChatMessageType.Text
                    && x.DeletedAtUtc == null && x.PinnedAtUtc == null
                    && db.ChatConversationMembers.Any(member => member.ConversationId == x.ConversationId && member.UserId == userId))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.PinnedAtUtc, now)
                    .SetProperty(x => x.PinnedByUserId, userId), cancellationToken)
            : await db.ChatMessages.Where(x => x.Id == messageId && x.Type == ChatMessageType.Text
                    && x.DeletedAtUtc == null && x.PinnedAtUtc != null
                    && db.ChatConversationMembers.Any(member => member.ConversationId == x.ConversationId && member.UserId == userId))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.PinnedAtUtc, (DateTime?)null)
                    .SetProperty(x => x.PinnedByUserId, (string?)null), cancellationToken);
        if (updated == 0) return;
        if (pinned)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                ConversationId = message.ConversationId, ChannelThreadId = message.ChannelThreadId,
                SenderUserId = userId,
                Type = ChatMessageType.PinNotice, Body = "pinned a message",
                ReplyToMessageId = messageId, SentAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await db.ChatConversations.Where(x => x.Id == message.ConversationId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastActivityAtUtc,
                    x => x.LastActivityAtUtc > now ? x.LastActivityAtUtc : now), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        var recipients = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == message.ConversationId).Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        notifications.Publish(recipients);
    }

    public async Task EditOwnMessageAsync(long messageId, string body, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        body = body.Trim();
        if (body.Length is < 1 or > 4000)
            throw new InvalidOperationException("Message must be 1 to 4000 characters.");

        var message = await db.ChatMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
        if (message is null || message.Type != ChatMessageType.Text || message.SenderUserId != userId
            || !await db.ChatConversationMembers.AnyAsync(
                x => x.ConversationId == message.ConversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You can only edit your own messages in conversations you belong to.");
        if (message.DeletedAtUtc is not null)
            throw new InvalidOperationException("Deleted messages cannot be edited.");
        if (message.Body == body) return;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var mentions = await ResolveMentionsAsync(db, message.ConversationId, userId,
            body, now, cancellationToken);
        if (mentions.Body.Length > 4000) throw new InvalidOperationException("Message must be 1 to 4000 characters.");
        if (message.Body == mentions.Body) return;
        await db.ChatMessageMentions.Where(x => x.MessageId == messageId).ExecuteDeleteAsync(cancellationToken);
        await db.ChatMessageTags.Where(x => x.MessageId == messageId).ExecuteDeleteAsync(cancellationToken);
        db.ChatMessageMentions.AddRange(mentions.Recipients.Select(id => new ChatMessageMention
        {
            MessageId = messageId, UserId = id
        }));
        db.ChatMessageTags.AddRange(mentions.Tags.Select(token => new ChatMessageTag
        {
            MessageId = messageId, Start = token.Start, Length = token.Length,
            Kind = token.Kind, UserId = token.UserId
        }));
        message.Body = mentions.Body;
        message.EditedAtUtc = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsCharacterEncodingFailure(exception))
        {
            throw new InvalidOperationException(UnsupportedDatabaseEncodingMessage, exception);
        }
        await db.ChatConversations.Where(x => x.Id == message.ConversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastActivityAtUtc,
                x => x.LastActivityAtUtc > now ? x.LastActivityAtUtc : now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var recipients = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == message.ConversationId).Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        notifications.Publish(recipients);
    }

    private static bool IsCharacterEncodingFailure(DbUpdateException exception) =>
        exception.GetBaseException() is PostgresException { SqlState: "22P05" };
}
