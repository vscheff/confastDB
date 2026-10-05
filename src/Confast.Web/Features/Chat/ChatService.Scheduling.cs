using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed partial class ChatService
{
    // Delivery and queue removal commit together. SKIP LOCKED lets multiple server
    // instances process the queue without ever delivering the same row twice.
    public async Task<int> DeliverDueMessagesAsync(CancellationToken cancellationToken = default)
    {
        var delivered = 0;
        for (var index = 0; index < 100; index++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var now = clock.GetUtcNow().UtcDateTime;
            var queued = (await db.Set<ChatScheduledMessage>().FromSqlInterpolated($"""
                SELECT * FROM chat_scheduled_messages
                WHERE failure IS NULL AND scheduled_at_utc <= {now}
                ORDER BY scheduled_at_utc, id LIMIT 1 FOR UPDATE SKIP LOCKED
                """).ToListAsync(cancellationToken)).SingleOrDefault();
            if (queued is null) break;
            await db.Entry(queued).Collection(x => x.Attachments).LoadAsync(cancellationToken);
            var allowed = await db.Users.AnyAsync(x => x.Id == queued.SenderUserId && x.IsActive, cancellationToken)
                && await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == queued.ConversationId
                    && x.UserId == queued.SenderUserId, cancellationToken);
            if (!allowed)
            {
                queued.Failure = "The sender is inactive or is no longer a conversation member.";
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                continue;
            }
            // A deleted reply target should not prevent the scheduled text being sent.
            var replyId = queued.ReplyToMessageId;
            if (replyId is long target && !await db.ChatMessages.AnyAsync(x => x.Id == target
                    && x.DeletedAtUtc == null, cancellationToken)) replyId = null;
            await transaction.CreateSavepointAsync("delivery", cancellationToken);
            try
            {
                await SaveOutgoingMessageAsync(db, queued.SenderUserId, queued.ConversationId, queued.Body,
                    queued.Attachments.Select(x => new ChatAttachmentUpload(x.FileName, x.Content)).ToArray(),
                    replyId, queued.ChannelThreadId, null, false, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                // Invalid drafts must not block all later queued messages indefinitely.
                await transaction.RollbackToSavepointAsync("delivery", cancellationToken);
                db.ChangeTracker.Clear();
                await db.Set<ChatScheduledMessage>().Where(x => x.Id == queued.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Failure, ex.Message.Substring(0, Math.Min(500, ex.Message.Length))), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                continue;
            }
            db.Remove(queued);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            delivered++;
            notifications.Publish(await db.ChatConversationMembers.Where(x => x.ConversationId == queued.ConversationId)
                .Select(x => x.UserId).ToArrayAsync(cancellationToken));
        }
        return delivered;
    }
}

public sealed class ChatMessageDeliveryWorker(IServiceScopeFactory scopes,
    ILogger<ChatMessageDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ChatService>().DeliverDueMessagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Leave the transaction rolled back so temporary outages can retry.
                logger.LogError(ex, "Scheduled chat message delivery failed; it will be retried.");
            }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
