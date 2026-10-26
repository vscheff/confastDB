using Confast.Web.Features.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed partial class ChatService
{
    public async Task DeleteOthersMessageAsync(long messageId, string reason, CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync(nameof(DeleteOthersMessageAsync), new("ChatMessage", messageId.ToString()),
            [Permissions.Chat.DeleteOthersMessages], cancellationToken);
        reason = reason?.Trim() ?? string.Empty;
        if (reason.Length is < 1 or > 1000 || reason.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidOperationException("Enter a moderation reason of 1 to 1000 characters.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var actorId = await PrepareChatAsync(db, operation, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize deletions with votes/reactions and lock membership until the audit commits.
        var message = (await db.ChatMessages.FromSqlInterpolated(
            $"SELECT * FROM chat_messages WHERE id = {messageId} FOR UPDATE").ToListAsync(cancellationToken)).SingleOrDefault();
        if (message is null || message.Type != ChatMessageType.Text || message.GiphyId is not null
            || message.SenderUserId is null || message.SenderUserId == actorId)
            throw new UnauthorizedAccessException("This message is not eligible for channel moderation.");
        var memberships = await db.ChatConversationMembers.FromSqlInterpolated($"""
            SELECT * FROM chat_conversation_members
            WHERE conversation_id = {message.ConversationId} AND user_id = {actorId} FOR SHARE
            """).ToListAsync(cancellationToken);
        if (memberships.Count != 1 || !await db.ChatConversations.AnyAsync(x => x.Id == message.ConversationId
                && x.Kind == ConversationKind.Channel, cancellationToken)
            || (message.ChannelThreadId is long threadId && !await db.ChatChannelThreads.AnyAsync(x =>
                x.Id == threadId && x.ConversationId == message.ConversationId, cancellationToken)))
            throw new UnauthorizedAccessException("Channel moderation requires current channel membership.");
        if (message.DeletedAtUtc is not null)
            throw new InvalidOperationException("This message has already been deleted.");
        var now = clock.GetUtcNow().UtcDateTime;
        db.Set<ChatModerationHistory>().Add(new()
        {
            MessageId = message.Id, ConversationId = message.ConversationId, ChannelThreadId = message.ChannelThreadId,
            ModeratorUserId = actorId, OriginalSenderUserId = message.SenderUserId,
            DeletedAtUtc = now, Reason = reason, OperationId = operation.Id, WasPinned = message.PinnedAtUtc is not null
        });
        message.DeletedAtUtc = now;
        message.DeletedByUserId = actorId;
        message.PinnedAtUtc = null;
        message.PinnedByUserId = null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish(await db.ChatConversationMembers.Where(x => x.ConversationId == message.ConversationId)
            .Select(x => x.UserId).ToArrayAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<ChatModerationHistory>> GetModerationHistoryAsync(long conversationId,
        CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync(nameof(GetModerationHistoryAsync), new("ChatChannel", conversationId.ToString()),
            [Permissions.Chat.DeleteOthersMessages], cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await PrepareChatAsync(db, operation, cancellationToken);
        await RequireChannelMemberAsync(db, conversationId, userId, cancellationToken);
        return await db.Set<ChatModerationHistory>().AsNoTracking().Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.Id).Take(200).ToArrayAsync(cancellationToken);
    }
}
