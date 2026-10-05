using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed partial class ChatService
{
    public static readonly IReadOnlyList<int> PollDurationHours = [1, 4, 8, 24, 72, 168, 336];

    public async Task<long> CreatePollAsync(long conversationId, string question,
        IReadOnlyList<ChatPollAnswerInput> answers, int durationHours = 24,
        bool allowMultipleAnswers = false, DateTime? startsAtUtc = null,
        long? channelThreadId = null, CancellationToken cancellationToken = default)
    {
        question = question.Trim();
        if (question.Length is < 1 or > 4000)
            throw new InvalidOperationException("Enter a question of 1 to 4000 characters.");
        if (answers.Count is < 2 or > 20)
            throw new InvalidOperationException("A poll needs between 2 and 20 answers.");
        var normalized = answers.Select(x => new ChatPollAnswerInput(x.Text.Trim(),
            string.IsNullOrWhiteSpace(x.Emoji) ? null : x.Emoji.Trim())).ToArray();
        if (normalized.Any(x => x.Text.Length is < 1 or > 200))
            throw new InvalidOperationException("Each answer needs 1 to 200 characters.");
        if (normalized.Any(x => x.Emoji is not null &&
            (x.Emoji.Length > 64 || ChatEmojiCatalog.FindFamilyForEmoji(x.Emoji) is null)))
            throw new InvalidOperationException("Choose a valid emoji for each answer, or leave it blank.");
        if (!PollDurationHours.Contains(durationHours))
            throw new InvalidOperationException("Choose one of the available poll durations.");
        var now = clock.GetUtcNow().UtcDateTime;
        var start = startsAtUtc ?? now;
        if (start.Kind != DateTimeKind.Utc || start < now)
            throw new InvalidOperationException("Choose a future start date and time.");
        if (start > DateTime.MaxValue.AddHours(-durationHours))
            throw new InvalidOperationException("Choose an earlier start date and time.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("Join this conversation before creating a poll.");
        if (channelThreadId is long threadId && !await db.ChatChannelThreads.AnyAsync(x =>
            x.Id == threadId && x.ConversationId == conversationId, cancellationToken))
            throw new InvalidOperationException("That thread does not belong to this channel.");
        var message = new ChatMessage { ConversationId = conversationId, ChannelThreadId = channelThreadId,
            SenderUserId = userId, Body = question, Type = ChatMessageType.Poll, SentAtUtc = now };
        var poll = new ChatPoll { Message = message, StartsAtUtc = start, EndsAtUtc = start.AddHours(durationHours),
            AllowMultipleAnswers = allowMultipleAnswers,
            Answers = normalized.Select((x, index) => new ChatPollAnswer { Text = x.Text, Emoji = x.Emoji, Position = index }).ToList() };
        db.Add(poll);
        await db.SaveChangesAsync(cancellationToken);
        await db.ChatConversations.Where(x => x.Id == conversationId).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.LastActivityAtUtc, x => x.LastActivityAtUtc > now ? x.LastActivityAtUtc : now), cancellationToken);
        if (channelThreadId is long id)
            await db.ChatChannelThreads.Where(x => x.Id == id).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.LastMessageAtUtc, x => x.LastMessageAtUtc > now ? x.LastMessageAtUtc : now), cancellationToken);
        await db.ChatConversationMembers.Where(x => x.ConversationId == conversationId && x.UserId == userId && x.IsManuallyUnread)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReadMessageId, (long?)message.Id)
                .SetProperty(x => x.IsManuallyUnread, false), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish(await db.ChatConversationMembers.Where(x => x.ConversationId == conversationId)
            .Select(x => x.UserId).ToArrayAsync(cancellationToken));
        return message.Id;
    }

    public async Task TogglePollVoteAsync(long messageId, long answerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize votes on the message, including concurrent requests from the same user.
        var message = (await db.ChatMessages.FromSqlInterpolated(
            $"SELECT * FROM chat_messages WHERE id = {messageId} FOR UPDATE").ToListAsync(cancellationToken)).SingleOrDefault();
        if (message is null || message.DeletedAtUtc is not null || !await db.ChatConversationMembers.AnyAsync(x =>
            x.ConversationId == message.ConversationId && x.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You cannot vote in this poll.");
        var poll = await db.Set<ChatPoll>().Include(x => x.Answers).SingleOrDefaultAsync(x => x.MessageId == messageId, cancellationToken)
            ?? throw new InvalidOperationException("This message is not a poll.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (now < poll.StartsAtUtc) throw new InvalidOperationException("This poll has not started yet.");
        if (now >= poll.EndsAtUtc) throw new InvalidOperationException("This poll is closed.");
        if (!poll.Answers.Any(x => x.Id == answerId)) throw new InvalidOperationException("That answer does not belong to this poll.");
        var votes = await db.Set<ChatPollVote>().Where(x => x.PollMessageId == messageId && x.UserId == userId).ToListAsync(cancellationToken);
        var existing = votes.SingleOrDefault(x => x.AnswerId == answerId);
        if (existing is not null) db.Remove(existing);
        else
        {
            if (!poll.AllowMultipleAnswers) db.RemoveRange(votes);
            db.Add(new ChatPollVote { PollMessageId = messageId, AnswerId = answerId, UserId = userId });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish(await db.ChatConversationMembers.Where(x => x.ConversationId == message.ConversationId)
            .Select(x => x.UserId).ToArrayAsync(cancellationToken));
    }

    private async Task<Dictionary<long, ChatPollRow>> ReadPollsAsync(AppDbContext db, long[] messageIds,
        string userId, CancellationToken cancellationToken)
    {
        var polls = await db.Set<ChatPoll>().AsNoTracking().Where(x => messageIds.Contains(x.MessageId))
            .Include(x => x.Answers).ThenInclude(x => x.Votes).AsSplitQuery().ToListAsync(cancellationToken);
        var voterIds = polls.SelectMany(x => x.Answers).SelectMany(x => x.Votes)
            .Select(x => x.UserId).Distinct().ToArray();
        var voters = await db.Users.AsNoTracking().Where(x => voterIds.Contains(x.Id))
            .Select(x => new ChatUser(x.Id, x.DisplayName)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        return polls.ToDictionary(x => x.MessageId, x => new ChatPollRow(x.StartsAtUtc, x.EndsAtUtc,
            x.AllowMultipleAnswers, now >= x.StartsAtUtc && now < x.EndsAtUtc, now >= x.StartsAtUtc,
            x.Answers.SelectMany(a => a.Votes).Select(v => v.UserId).Distinct().Count(),
            x.Answers.OrderBy(a => a.Position).Select(a => new ChatPollAnswerRow(a.Id, a.Text, a.Emoji,
                a.Votes.Count, a.Votes.Any(v => v.UserId == userId),
                a.Votes.Select(v => voters[v.UserId]).OrderBy(v => v.Name).ThenBy(v => v.Id).ToArray())).ToArray()));
    }
}
