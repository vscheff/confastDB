using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed partial class ChatService
{
    private static void ValidateGiphyId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidOperationException("Choose a valid GIPHY GIF.");
    }

    public async Task<IReadOnlyList<string>> GetGifFavoritesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await db.Set<ChatGifFavorite>().AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.GiphyId).Select(x => x.GiphyId)
            .ToArrayAsync(cancellationToken);
    }

    public async Task SetGifFavoriteAsync(string giphyId, bool favorite, CancellationToken cancellationToken = default)
    {
        ValidateGiphyId(giphyId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (favorite)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            // Explicit desired state plus a unique key makes repeated/concurrent starring idempotent.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO chat_gif_favorites (user_id, giphy_id, created_at_utc) VALUES ({userId}, {giphyId}, {now}) ON CONFLICT DO NOTHING", cancellationToken);
        }
        else
            await db.Set<ChatGifFavorite>().Where(x => x.UserId == userId && x.GiphyId == giphyId)
                .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<long> SendGifAsync(long conversationId, string giphyId, long? replyToMessageId = null,
        long? channelThreadId = null, CancellationToken cancellationToken = default)
    {
        ValidateGiphyId(giphyId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var id = await SaveOutgoingMessageAsync(db, userId, conversationId, "Shared a GIF", [],
            replyToMessageId, channelThreadId, null, true, cancellationToken, giphyId);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish(await db.ChatConversationMembers.Where(x => x.ConversationId == conversationId)
            .Select(x => x.UserId).ToArrayAsync(cancellationToken), id);
        return id;
    }
}
