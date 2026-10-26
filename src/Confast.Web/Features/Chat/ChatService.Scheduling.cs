using System.Data;
using Confast.Web.Features.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.Chat;

public sealed partial class ChatService
{
    // Cancellation is self-scoped: Access suffices even after ScheduleMessages is removed.
    public async Task CancelScheduledMessageAsync(long id, CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync(nameof(CancelScheduledMessageAsync), new("Chat"), [Permissions.Chat.Access], cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var draft = (await db.Set<ChatScheduledMessage>().FromSqlInterpolated($"""
            SELECT * FROM chat_scheduled_messages WHERE id = {id} AND sender_user_id = {operation.ActorUserId} FOR UPDATE
            """).ToListAsync(cancellationToken)).SingleOrDefault()
            ?? throw new InvalidOperationException("That scheduled message is unavailable.");
        if (draft.State == ScheduledChatState.Delivered) throw new InvalidOperationException("That message has already been delivered.");
        if (draft.State != ScheduledChatState.Canceled)
        {
            FinishDelivery(draft, ScheduledChatState.Canceled, "Canceled", "Canceled by its sender.");
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish([operation.ActorUserId]);
    }

    // Fixed worker purpose; no caller-supplied actor, destination, override or mutation delegate.
    // SKIP LOCKED owns the attempt; insertion and terminal receipt commit together.
    internal async Task<int> DeliverDueMessagesAsync(CancellationToken cancellationToken = default)
    {
        var delivered = 0;
        for (var index = 0; index < 100; index++)
        {
            long? attemptedId = null;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
                var now = clock.GetUtcNow().UtcDateTime;
                var draft = (await db.Set<ChatScheduledMessage>().FromSqlInterpolated($"""
                    SELECT * FROM chat_scheduled_messages WHERE state = 0 AND scheduled_at_utc <= {now}
                        AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= {now})
                    ORDER BY scheduled_at_utc, id LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).ToListAsync(cancellationToken)).SingleOrDefault();
                if (draft is null) break;
                attemptedId = draft.Id;
                draft.AttemptCount++;
                var failure = draft.RequestId == Guid.Empty || draft.Purpose != ChatScheduledMessage.DeliveryPurpose || draft.InstallationGeneration is null
                    ? "UnverifiedIntent" : now >= draft.ValidUntilUtc ? "Expired" : null;
                if (failure is null)
                {
                    try
                    {
                        var snapshot = await permissionEvaluator.EvaluateScheduledChatSenderAsync(db, draft, cancellationToken);
                        using var operation = new AuthorizedOperation(ChatScheduledMessage.DeliveryPurpose,
                            new("ChatScheduledMessage", draft.RequestId.ToString()), PermissionCatalog.ExpandRequirements([Permissions.Chat.ScheduleMessages]), snapshot);
                        operation.Require(ChatScheduledMessage.DeliveryPurpose, operation.Scope, Permissions.Chat.ScheduleMessages);
                    }
                    catch (AuthorizationDeniedException ex) when (ex.Denial != AuthorizationDenial.DatabaseUnavailable)
                    { failure = ex.Denial == AuthorizationDenial.DelegationDenied ? "InstallationChanged" : "AuthorizationLost"; }
                }
                if (failure is null && (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == draft.ConversationId
                        && x.UserId == draft.SenderUserId, cancellationToken)
                    || !await db.ChatConversations.AnyAsync(x => x.Id == draft.ConversationId, cancellationToken)
                    || (draft.ChannelThreadId is long thread && !await db.ChatChannelThreads.AnyAsync(x => x.Id == thread
                        && x.ConversationId == draft.ConversationId, cancellationToken)))) failure = "DestinationUnavailable";
                if (failure is not null) FinishDelivery(draft, ScheduledChatState.Failed, failure, failure switch
                {
                    "Expired" => "The delivery window ended seven days after the scheduled time.",
                    "InstallationChanged" => "The installation changed. Review and schedule this draft again.",
                    "UnverifiedIntent" => "This older draft has no verified delegation. Review and schedule it again.",
                    "DestinationUnavailable" => "The destination or sender's membership is no longer available.",
                    _ => "The sender is no longer authorized to deliver this scheduled message."
                });
                else
                {
                    await db.Entry(draft).Collection(x => x.Attachments).LoadAsync(cancellationToken);
                    var replyId = draft.ReplyToMessageId;
                    // Preserve existing behavior: deleted replies do not discard scheduled text.
                    if (replyId is long target && !await db.ChatMessages.AnyAsync(x => x.Id == target && x.DeletedAtUtc == null, cancellationToken)) replyId = null;
                    await transaction.CreateSavepointAsync("delivery", cancellationToken);
                    try
                    {
                        draft.DeliveredMessageId = await SaveOutgoingMessageAsync(db, draft.SenderUserId, draft.ConversationId, draft.Body,
                            draft.Attachments.Select(x => new ChatAttachmentUpload(x.FileName, x.Content)).ToArray(),
                            replyId, draft.ChannelThreadId, null, false, cancellationToken);
                        FinishDelivery(draft, ScheduledChatState.Delivered, null, null);
                    }
                    catch (Exception ex) when (!IsTransientDeliveryFailure(ex) && (ex is InvalidOperationException or UnauthorizedAccessException
                        || ex is DbUpdateException { InnerException: PostgresException { SqlState: "23502" or "23503" or "23505" or "23514" or "22001" or "22021" } }))
                    {
                        await transaction.RollbackToSavepointAsync("delivery", cancellationToken);
                        db.ChangeTracker.Clear();
                        draft = await db.Set<ChatScheduledMessage>().SingleAsync(x => x.Id == attemptedId, cancellationToken);
                        draft.AttemptCount++;
                        FinishDelivery(draft, ScheduledChatState.Failed, ex is UnauthorizedAccessException ? "DestinationUnavailable" : "InvalidData",
                            "This draft can no longer be delivered. Review its destination, text and attachments.");
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (draft.State == ScheduledChatState.Delivered)
                {
                    delivered++;
                    notifications.Publish(await db.ChatConversationMembers.Where(x => x.ConversationId == draft.ConversationId)
                        .Select(x => x.UserId).ToArrayAsync(cancellationToken), draft.DeliveredMessageId);
                }
                notifications.Publish([draft.SenderUserId]);
            }
            catch (Exception ex) when (IsTransientDeliveryFailure(ex) && !cancellationToken.IsCancellationRequested)
            {
                // Rollback disposed first. Re-lock to avoid changing another worker's committed result.
                if (attemptedId is long id) await RecordTransientDeliveryFailureAsync(id, cancellationToken);
                logger?.LogWarning("Transient scheduled chat database failure for queue row {QueueId}.", attemptedId);
                break;
            }
            catch (Exception ex) when (attemptedId is not null && !cancellationToken.IsCancellationRequested)
            {
                // Unexpected per-request failures also have a bound; do not poison the queue forever.
                await RecordTransientDeliveryFailureAsync(attemptedId.Value, cancellationToken);
                logger?.LogError("Scheduled chat queue row {QueueId} failed with {ExceptionType}; bounded recovery recorded.", attemptedId, ex.GetType().Name);
                break;
            }
        }
        return delivered;
    }

    private static bool IsTransientDeliveryFailure(Exception ex) => ex is NpgsqlException { IsTransient: true } or TimeoutException
        || ex.InnerException is { } inner && IsTransientDeliveryFailure(inner);

    private async Task RecordTransientDeliveryFailureAsync(long id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var draft = (await db.Set<ChatScheduledMessage>().FromSqlInterpolated($"SELECT * FROM chat_scheduled_messages WHERE id = {id} FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault();
        if (draft?.State == ScheduledChatState.Pending)
        {
            draft.AttemptCount++;
            draft.FailureCode = "TransientInfrastructure";
            var now = clock.GetUtcNow().UtcDateTime;
            if (draft.AttemptCount >= 5 || now >= draft.ValidUntilUtc)
                FinishDelivery(draft, ScheduledChatState.Failed, "RetryExhausted", "Delivery failed after bounded infrastructure retries. Review and schedule again.");
            else draft.NextAttemptAtUtc = now.AddSeconds(15 * Math.Pow(2, draft.AttemptCount - 1));
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private void FinishDelivery(ChatScheduledMessage draft, ScheduledChatState state, string? code, string? failure)
    {
        draft.State = state; draft.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        draft.NextAttemptAtUtc = null; draft.FailureCode = code; draft.Failure = failure;
    }
}

public sealed class ChatMessageDeliveryWorker(IServiceScopeFactory scopes, ILogger<ChatMessageDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ChatService>().DeliverDueMessagesAsync(stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                failures = Math.Min(failures + 1, 6);
                // Exception details from database validation can contain message content.
                logger.LogError("Scheduled chat delivery infrastructure failed; backing off (failure {FailureCount}).", failures);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, failures))), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
