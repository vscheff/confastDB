using System.Collections.Concurrent;
using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Confast.Web.Features.Chat;

public sealed record ChatUser(string Id, string Name);
public sealed record ChatConversationRow(long Id, ConversationKind Kind, ChannelVisibility? Visibility,
    string Name, string? Preview, DateTime ActivityAtUtc, int UnreadCount, bool IsOwner);
public sealed record ChatMessageRow(long Id, string? SenderUserId, string SenderName, string? Body,
    DateTime SentAtUtc, DateTime? EditedAtUtc, bool IsDeleted);
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
        return userId;
    }

    public async Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await RequireUserAsync(db, cancellationToken);
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
        var ids = initialUserIds.Where(x => !string.IsNullOrWhiteSpace(x)).Append(userId)
            .Distinct(StringComparer.Ordinal).ToArray();
        var activeCount = await db.Users.CountAsync(x => ids.Contains(x.Id) && x.IsActive, cancellationToken);
        if (activeCount != ids.Length) throw new InvalidOperationException("All channel members must be active users.");
        var now = clock.GetUtcNow().UtcDateTime;
        var conversation = new Conversation
        {
            Kind = ConversationKind.Channel, Name = name, Visibility = visibility,
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
                m.IsOwner))
            .ToListAsync(cancellationToken);
    }

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

    public async Task<IReadOnlyList<ChatConversationRow>> DiscoverPublicChannelsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await db.ChatConversations.AsNoTracking()
            .Where(x => x.Kind == ConversationKind.Channel && x.Visibility == ChannelVisibility.Public
                && !x.Members.Any(m => m.UserId == userId))
            .OrderBy(x => x.Name)
            .Select(x => new ChatConversationRow(x.Id, x.Kind, x.Visibility, x.Name!, null,
                x.LastActivityAtUtc, 0, false))
            .ToListAsync(cancellationToken);
    }

    public async Task JoinPublicAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var channel = await db.ChatConversations.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == conversationId && x.Kind == ConversationKind.Channel
                && x.Visibility == ChannelVisibility.Public, cancellationToken);
        if (channel is null) throw new UnauthorizedAccessException("Public channel not found.");
        if (await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId && x.UserId == userId, cancellationToken)) return;
        var latest = await db.ChatMessages.Where(x => x.ConversationId == conversationId)
            .MaxAsync(x => (long?)x.Id, cancellationToken);
        db.ChatConversationMembers.Add(new ConversationMember
        {
            ConversationId = conversationId, UserId = userId,
            JoinedAtUtc = clock.GetUtcNow().UtcDateTime, LastReadMessageId = latest
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { }
        notifications.Publish([userId]);
    }

    public async Task LeavePublicAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var member = await db.ChatConversationMembers.Include(x => x.Conversation)
            .SingleOrDefaultAsync(x => x.ConversationId == conversationId && x.UserId == userId, cancellationToken);
        if (member is null || member.Conversation.Visibility != ChannelVisibility.Public
            || member.Conversation.Kind != ConversationKind.Channel)
            throw new UnauthorizedAccessException("You do not belong to this public channel.");
        db.ChatConversationMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        notifications.Publish([userId]);
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
                x.DeletedAtUtc == null ? x.Body : null, x.SentAtUtc, x.EditedAtUtc, x.DeletedAtUtc != null))
            .ToListAsync(cancellationToken);
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
