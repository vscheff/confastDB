using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Confast.Web.Features.Chat;

public sealed record ChatUser(string Id, string Name);
public sealed record ChatConversationRow(long Id, ConversationKind Kind, ChannelVisibility? Visibility,
    string Name, string? Preview, DateTime ActivityAtUtc, int UnreadCount, bool IsOwner,
    long? ChannelGroupId, int ChannelSortOrder, string? OtherUserId);
public sealed record ChatChannelGroupRow(long Id, string Name, int SortOrder, bool CanManage);
public sealed record ChatMessageRow(long Id, string? SenderUserId, string SenderName, string? Body,
    DateTime SentAtUtc, DateTime? EditedAtUtc, bool IsDeleted,
    IReadOnlyList<ChatReactionRow> Reactions);
public sealed record ChatReactionRow(string Emoji, IReadOnlyList<ChatUser> Users);
public sealed record ChatThread(ChatConversationRow Conversation, IReadOnlyList<ChatMessageRow> Messages,
    IReadOnlyList<ChatUser> Members);

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

public sealed class ChatService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICurrentUser currentUser,
    ChatNotifications notifications,
    TimeProvider clock)
{
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
        var userId = await RequireUserAsync(db, cancellationToken);
        return await db.Users.AsNoTracking().Where(x => x.IsActive && x.Id != userId)
            .OrderBy(x => x.DisplayName).Select(x => new ChatUser(x.Id, x.DisplayName))
            .ToListAsync(cancellationToken);
    }

    public async Task<long> OpenDirectAsync(string otherUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (otherUserId == userId) throw new InvalidOperationException("You cannot message yourself.");
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
            Members =
            [
                new() { UserId = userId, JoinedAtUtc = now },
                new() { UserId = otherUserId, JoinedAtUtc = now }
            ]
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
        IEnumerable<string> initialUserIds, CancellationToken cancellationToken = default)
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
        var sortOrder = (await db.ChatConversations
            .Where(x => x.Kind == ConversationKind.Channel && x.ChannelGroupId == null)
            .MaxAsync(x => (int?)x.ChannelSortOrder, cancellationToken) ?? -1) + 1;
        var conversation = new Conversation
        {
            Kind = ConversationKind.Channel, Name = name, Visibility = visibility,
            ChannelSortOrder = sortOrder,
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
                    ? m.Conversation.Members.Where(x => x.UserId != userId)
                        .Select(x => x.User.DisplayName).FirstOrDefault() ?? "Direct Message"
                    : m.Conversation.Name!,
                db.ChatMessages.Where(x => x.ConversationId == m.ConversationId)
                    .OrderByDescending(x => x.Id)
                    .Select(x => x.DeletedAtUtc == null ? x.Body : "Message deleted").FirstOrDefault(),
                m.Conversation.LastActivityAtUtc,
                db.ChatMessages.Count(x => x.ConversationId == m.ConversationId
                    && x.Id > (m.LastReadMessageId ?? 0) && x.SenderUserId != userId),
                m.IsOwner, m.Conversation.ChannelGroupId, m.Conversation.ChannelSortOrder,
                m.Conversation.Kind == ConversationKind.Direct
                    ? m.Conversation.Members.Where(x => x.UserId != userId)
                        .Select(x => x.UserId).FirstOrDefault()
                    : null))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatChannelGroupRow>> GetChannelGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await db.ChatChannelGroups.AsNoTracking()
            .Where(group => group.Channels.Any(channel => channel.Members.Any(member => member.UserId == userId))
                || (!group.Channels.Any() && group.CreatedByUserId == userId))
            .OrderBy(group => group.SortOrder).ThenBy(group => group.Id)
            .Select(group => new ChatChannelGroupRow(group.Id, group.Name, group.SortOrder,
                !group.Channels.Any(channel => !channel.Members.Any(member => member.UserId == userId))
                && (group.Channels.Any() || group.CreatedByUserId == userId)))
            .ToListAsync(cancellationToken);
    }

    public async Task<long> CreateChannelGroupAsync(string name, long? channelId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        name = ValidateGroupName(name);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        if (await db.ChatChannelGroups.AnyAsync(x => x.Name.ToUpper() == name.ToUpper() &&
            (x.Channels.Any(channel => channel.Members.Any(member => member.UserId == userId))
                || (!x.Channels.Any() && x.CreatedByUserId == userId)), cancellationToken))
            throw new InvalidOperationException("A channel group with that name already exists.");
        var group = new ChatChannelGroup
        {
            Name = name,
            SortOrder = (await db.ChatChannelGroups.MaxAsync(x => (int?)x.SortOrder, cancellationToken) ?? -1) + 1,
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
        if (await db.ChatChannelGroups.AnyAsync(x => x.Id != groupId && x.Name.ToUpper() == name.ToUpper() &&
            (x.Channels.Any(channel => channel.Members.Any(member => member.UserId == userId))
                || (!x.Channels.Any() && x.CreatedByUserId == userId)), cancellationToken))
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
        var nextOrder = (await db.ChatConversations.Where(x => x.Kind == ConversationKind.Channel && x.ChannelGroupId == null)
            .MaxAsync(x => (int?)x.ChannelSortOrder, cancellationToken) ?? -1) + 1;
        var channels = await db.ChatConversations.Where(x => x.ChannelGroupId == groupId)
            .OrderBy(x => x.ChannelSortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            channel.ChannelGroupId = null;
            channel.ChannelSortOrder = nextOrder++;
        }
        db.ChatChannelGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishLayoutChangedAsync(db, cancellationToken);
    }

    public async Task MoveChannelAsync(long channelId, long? targetGroupId, long? beforeChannelId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        var channel = await RequireChannelMemberAsync(db, channelId, userId, cancellationToken);
        if (targetGroupId is long groupId && !await CanSeeGroupAsync(db, groupId, userId, cancellationToken))
            throw new UnauthorizedAccessException("You cannot move a channel into that group.");
        if (beforeChannelId == channelId) return;
        if (beforeChannelId is long beforeId)
        {
            var before = await RequireChannelMemberAsync(db, beforeId, userId, cancellationToken);
            if (before.ChannelGroupId != targetGroupId)
                throw new InvalidOperationException("The drop target is no longer in that group.");
        }

        var oldGroupId = channel.ChannelGroupId;
        var channels = await db.ChatConversations
            .Where(x => x.Kind == ConversationKind.Channel &&
                (x.ChannelGroupId == oldGroupId || x.ChannelGroupId == targetGroupId))
            .OrderBy(x => x.ChannelSortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var source = channels.Where(x => x.ChannelGroupId == oldGroupId).ToList();
        var target = oldGroupId == targetGroupId ? source : channels.Where(x => x.ChannelGroupId == targetGroupId).ToList();
        source.RemoveAll(x => x.Id == channelId);
        var insertAt = beforeChannelId is long targetId
            ? target.FindIndex(x => x.Id == targetId) : target.Count;
        if (insertAt < 0) throw new InvalidOperationException("The drop target is no longer available.");
        target.Insert(insertAt, channel);
        for (var index = 0; index < source.Count; index++) source[index].ChannelSortOrder = index;
        for (var index = 0; index < target.Count; index++)
        {
            target[index].ChannelGroupId = targetGroupId;
            target[index].ChannelSortOrder = index;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishLayoutChangedAsync(db, cancellationToken);
    }

    public async Task MoveChannelGroupAsync(long groupId, long? beforeGroupId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        if (!await CanSeeGroupAsync(db, groupId, userId, cancellationToken) ||
            (beforeGroupId is long beforeId && !await CanSeeGroupAsync(db, beforeId, userId, cancellationToken)))
            throw new UnauthorizedAccessException("You cannot reorder that channel group.");
        if (beforeGroupId == groupId) return;
        var groups = await db.ChatChannelGroups.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var moving = groups.Single(x => x.Id == groupId);
        groups.Remove(moving);
        var insertAt = beforeGroupId is long targetId ? groups.FindIndex(x => x.Id == targetId) : groups.Count;
        if (insertAt < 0) throw new InvalidOperationException("The group is no longer available.");
        groups.Insert(insertAt, moving);
        for (var index = 0; index < groups.Count; index++) groups[index].SortOrder = index;
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

    private static async Task<Conversation> RequireChannelMemberAsync(AppDbContext db, long channelId,
        string userId, CancellationToken cancellationToken) =>
        await db.ChatConversations.SingleOrDefaultAsync(x => x.Id == channelId && x.Kind == ConversationKind.Channel
            && x.Members.Any(member => member.UserId == userId), cancellationToken)
        ?? throw new UnauthorizedAccessException("You do not belong to that channel.");

    private static Task<bool> CanSeeGroupAsync(AppDbContext db, long groupId, string userId,
        CancellationToken cancellationToken) => db.ChatChannelGroups.AnyAsync(x => x.Id == groupId &&
            (x.Channels.Any(channel => channel.Members.Any(member => member.UserId == userId))
                || (!x.Channels.Any() && x.CreatedByUserId == userId)), cancellationToken);

    private static async Task<ChatChannelGroup> RequireManageableGroupAsync(AppDbContext db, long groupId,
        string userId, CancellationToken cancellationToken)
    {
        var group = await db.ChatChannelGroups.SingleOrDefaultAsync(x => x.Id == groupId &&
            (x.Channels.Any() || x.CreatedByUserId == userId), cancellationToken)
            ?? throw new UnauthorizedAccessException("That channel group is unavailable.");
        if (!await db.ChatConversations.AnyAsync(x => x.ChannelGroupId == groupId, cancellationToken)
            && group.CreatedByUserId != userId)
            throw new UnauthorizedAccessException("That channel group is unavailable.");
        if (await db.ChatConversations.AnyAsync(x => x.ChannelGroupId == groupId &&
            !x.Members.Any(member => member.UserId == userId), cancellationToken))
            throw new UnauthorizedAccessException("You must belong to every channel in the group to manage it.");
        return group;
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
            .Join(db.ChatMessages.AsNoTracking().Where(x => x.SenderUserId != userId),
                member => member.ConversationId,
                message => message.ConversationId,
                (member, message) => new { message.Id, member.LastReadMessageId })
            .LongCountAsync(x => x.Id > (x.LastReadMessageId ?? 0), cancellationToken);
        return (int)Math.Min(total, int.MaxValue);
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

    public async Task<ChatThread> GetThreadAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this conversation.");
        var conversation = (await GetConversationsAsync(cancellationToken)).Single(x => x.Id == conversationId);
        var messages = await db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.Id).Take(100).OrderBy(x => x.Id)
            .Select(x => new ChatMessageRow(x.Id, x.SenderUserId,
                x.SenderUser == null ? "System" : x.SenderUser.DisplayName,
                x.DeletedAtUtc == null ? x.Body : null, x.SentAtUtc, x.EditedAtUtc, x.DeletedAtUtc != null,
                Array.Empty<ChatReactionRow>()))
            .ToListAsync(cancellationToken);
        var messageIds = messages.Select(x => x.Id).ToArray();
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
        messages = messages.Select(x => x with
        {
            Reactions = reactionsByMessage.GetValueOrDefault(x.Id) ?? []
        }).ToList();
        var members = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == conversationId)
            .OrderBy(x => x.User.DisplayName)
            .Select(x => new ChatUser(x.UserId, x.User.DisplayName))
            .ToListAsync(cancellationToken);
        return new ChatThread(conversation, messages, members);
    }

    public async Task<long> SendAsync(long conversationId, string body, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        body = body.Trim();
        if (body.Length is < 1 or > 4000) throw new InvalidOperationException("Message must be 1 to 4000 characters.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("Join this conversation before sending a message.");
        var now = clock.GetUtcNow().UtcDateTime;
        var message = new ChatMessage
        {
            ConversationId = conversationId, SenderUserId = userId, Body = body,
            SentAtUtc = now, Type = ChatMessageType.Text
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
        await db.ChatConversations.Where(x => x.Id == conversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastActivityAtUtc,
                x => x.LastActivityAtUtc > now ? x.LastActivityAtUtc : now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var recipients = await db.ChatConversationMembers.AsNoTracking()
            .Where(x => x.ConversationId == conversationId).Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        notifications.Publish(recipients);
        return message.Id;
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
                && (x.LastReadMessageId == null || x.LastReadMessageId < throughMessageId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastReadMessageId,
                (long?)throughMessageId), cancellationToken);
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
                && (x.LastReadMessageId == null || x.LastReadMessageId < latestMessageId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastReadMessageId,
                latestMessageId), cancellationToken);
        if (updated > 0) notifications.Publish([userId]);
        return latestMessageId;
    }

    public async Task DeleteOwnMessageAsync(long messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var message = await db.ChatMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
        if (message is null || message.SenderUserId != userId
            || !await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == message.ConversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You can only delete your own messages in conversations you belong to.");
        if (message.DeletedAtUtc is not null) return;
        message.DeletedAtUtc = clock.GetUtcNow().UtcDateTime;
        message.DeletedByUserId = userId;
        await db.SaveChangesAsync(cancellationToken);
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
        if (message is null || message.SenderUserId != userId
            || !await db.ChatConversationMembers.AnyAsync(
                x => x.ConversationId == message.ConversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You can only edit your own messages in conversations you belong to.");
        if (message.DeletedAtUtc is not null)
            throw new InvalidOperationException("Deleted messages cannot be edited.");
        if (message.Body == body) return;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        message.Body = body;
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
