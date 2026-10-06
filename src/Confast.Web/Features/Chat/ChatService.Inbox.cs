using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed record ChatInboxMessage(long Id, long ConversationId, long? ChannelThreadId,
    string SenderUserId, string SenderName, string Body, DateTime AtUtc,
    IReadOnlyList<string> AttachmentNames, string? Failure = null, bool IsMention = false);

public sealed partial class ChatService
{
    public async Task<ChatInboxMessage?> GetIncomingMessagePreviewAsync(long messageId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var preference = await db.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new { x.PresencePreference, x.PresencePreferenceExpiresAtUtc })
            .SingleAsync(cancellationToken);
        if (UserPresenceService.EffectivePreference(preference.PresencePreference,
                preference.PresencePreferenceExpiresAtUtc, clock.GetUtcNow().UtcDateTime)
            == UserPresencePreference.DoNotDisturb) return null;
        var preview = await db.ChatMessages.AsNoTracking()
            .Where(x => x.Id == messageId && x.DeletedAtUtc == null && x.SenderUserId != userId
                && (x.Type == ChatMessageType.Text || x.Type == ChatMessageType.Poll)
                && db.ChatConversationMembers.Any(member => member.ConversationId == x.ConversationId
                    && member.UserId == userId))
            .Select(x => new ChatInboxMessage(x.Id, x.ConversationId, x.ChannelThreadId,
                x.SenderUserId ?? "", x.SenderUser != null ? x.SenderUser.DisplayName : "System",
                x.Body, x.SentAtUtc, x.Attachments.Select(a => a.FileName).ToArray(), null))
            .SingleOrDefaultAsync(cancellationToken);
        if (preview is null) return null;
        var kind = await db.ChatConversations.Where(x => x.Id == preview.ConversationId)
            .Select(x => x.Kind).SingleAsync(cancellationToken);
        if (kind == ConversationKind.Direct) return preview;
        var settings = await LoadNotificationSettingsAsync(db, userId, cancellationToken);
        var mentioned = await db.ChatMessages.AnyAsync(x => x.Id == messageId
            && (x.Mentions.Any(m => m.UserId == userId)
                || (x.ReplyToMessage != null && x.ReplyToMessage.SenderUserId == userId)), cancellationToken);
        return settings.Channels.TryGetValue(preview.ConversationId, out var setting)
            && setting.AllowsPopup(mentioned) ? preview with { IsMention = mentioned } : null;
    }

    // Use the same read boundary as the sidebar. Reading previews never advances it.
    public async Task<IReadOnlyList<ChatInboxMessage>> GetInboxMessagesAsync(bool mentionsOnly = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var query = db.ChatMessages.AsNoTracking().Where(message => message.DeletedAtUtc == null
            && !message.Reads.Any(read => read.UserId == userId)
            && (message.ChannelThreadId == null || message.Type == ChatMessageType.ThreadNotice)
            && db.ChatConversationMembers.Any(member => member.UserId == userId
                && member.ConversationId == message.ConversationId
                && ((message.Id > (member.LastReadMessageId ?? 0) && message.SenderUserId != userId)
                    || (!mentionsOnly && member.IsManuallyUnread
                        && !db.ChatMessages.Any(unread => unread.ConversationId == member.ConversationId
                            && unread.DeletedAtUtc == null && unread.SenderUserId != userId
                            && !unread.Reads.Any(read => read.UserId == userId)
                            && unread.Id > (member.LastReadMessageId ?? 0)
                            && (unread.ChannelThreadId == null || unread.Type == ChatMessageType.ThreadNotice))
                        && message.Id == db.ChatMessages.Where(latest => latest.ConversationId == member.ConversationId
                            && latest.DeletedAtUtc == null && latest.ChannelThreadId == null
                            && (latest.Type == ChatMessageType.Text || latest.Type == ChatMessageType.Poll))
                            .Max(latest => (long?)latest.Id))))
            && (!mentionsOnly || message.Mentions.Any(mention => mention.UserId == userId)));
        return await query.OrderByDescending(x => x.Id).Take(100)
            .Select(x => new ChatInboxMessage(x.Id, x.ConversationId, x.ChannelThreadId,
                x.SenderUserId ?? "", x.SenderUser != null ? x.SenderUser.DisplayName : "System",
                x.Body, x.SentAtUtc, x.Attachments.Select(a => a.FileName).ToArray(), null))
            .ToListAsync(cancellationToken);
    }

    public async Task MarkMessageReadAsync(long conversationId, long messageId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize with manual unread and normal read-boundary updates for this member.
        var member = (await db.ChatConversationMembers.FromSqlInterpolated($"""
            SELECT * FROM chat_conversation_members
            WHERE conversation_id = {conversationId} AND user_id = {userId} FOR UPDATE
            """).ToListAsync(cancellationToken)).SingleOrDefault()
            ?? throw new UnauthorizedAccessException("You do not belong to this conversation.");
        if (!await db.ChatMessages.AnyAsync(x => x.ConversationId == conversationId
                && x.Id == messageId && x.DeletedAtUtc == null, cancellationToken))
            throw new InvalidOperationException("That message is no longer available in this conversation.");
        if (messageId > (member.LastReadMessageId ?? 0))
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO chat_message_reads (conversation_id, message_id, user_id)
                VALUES ({conversationId}, {messageId}, {userId})
                ON CONFLICT (user_id, message_id) DO NOTHING
                """, cancellationToken);
        member.IsManuallyUnread = false;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish([userId]);
    }

    public async Task MarkAllUnreadReadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Lock in a stable order and serialize with individual reads and manual unread.
        var members = await db.ChatConversationMembers.FromSqlInterpolated($"""
            SELECT * FROM chat_conversation_members WHERE user_id = {userId}
            ORDER BY conversation_id FOR UPDATE
            """).ToListAsync(cancellationToken);
        var ids = members.Select(x => x.ConversationId).ToArray();
        // Capture boundaries once. Messages arriving after this snapshot stay unread.
        var latest = await db.ChatMessages.Where(x => ids.Contains(x.ConversationId))
            .GroupBy(x => x.ConversationId)
            .Select(x => new { ConversationId = x.Key, MessageId = x.Max(m => m.Id) })
            .ToDictionaryAsync(x => x.ConversationId, x => x.MessageId, cancellationToken);
        foreach (var member in members)
        {
            if (latest.TryGetValue(member.ConversationId, out var messageId))
                member.LastReadMessageId = messageId;
            member.IsManuallyUnread = false;
        }
        await db.SaveChangesAsync(cancellationToken);
        await db.Set<ChatMessageRead>().Where(read => read.UserId == userId
            && ids.Contains(read.ConversationId)
            && db.ChatConversationMembers.Any(member => member.UserId == userId
                && member.ConversationId == read.ConversationId && read.MessageId <= member.LastReadMessageId))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish([userId]);
    }

    public async Task<IReadOnlyList<ChatInboxMessage>> GetScheduledMessagesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await db.Set<ChatScheduledMessage>().AsNoTracking()
            .Where(x => x.SenderUserId == userId)
            .OrderBy(x => x.ScheduledAtUtc).ThenBy(x => x.Id)
            .Select(x => new ChatInboxMessage(x.Id, x.ConversationId, x.ChannelThreadId, userId,
                db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).First(),
                x.Body, x.ScheduledAtUtc, x.Attachments.Select(a => a.FileName).ToArray(), x.Failure))
            .ToListAsync(cancellationToken);
    }
}
