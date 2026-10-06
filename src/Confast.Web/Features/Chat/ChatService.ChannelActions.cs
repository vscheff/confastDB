using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed partial class ChatService
{
    public async Task SetChannelPinnedToTopAsync(long channelId, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await RequireChannelMemberAsync(db, channelId, userId, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        await db.ChatConversationMembers.Where(x => x.ConversationId == channelId && x.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PinnedToTopAtUtc,
                x => pinned ? x.PinnedToTopAtUtc ?? now : null), cancellationToken);
        notifications.Publish([userId]);
    }

    public async Task<long> DuplicateChannelAsync(long sourceId, string name, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 120) throw new InvalidOperationException("Channel name must be 1 to 120 characters.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        var source = await RequireChannelMemberAsync(db, sourceId, userId, cancellationToken);
        var members = await db.ChatConversationMembers.AsNoTracking().Where(x => x.ConversationId == sourceId)
            .Select(x => new { x.UserId, x.IsOwner }).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        // Copy access and ownership, not messages, read state, pins, or notification preferences.
        var duplicate = new Conversation
        {
            Kind = ConversationKind.Channel, Name = name, Topic = source.Topic, Visibility = source.Visibility,
            ChannelGroupId = source.ChannelGroupId,
            ChannelSortOrder = await NextSiblingOrderAsync(db, source.ChannelGroupId, cancellationToken),
            CreatedByUserId = userId, CreatedAtUtc = now, LastActivityAtUtc = now,
            Members = members.Select(x => new ConversationMember
            {
                UserId = x.UserId, IsOwner = x.IsOwner || x.UserId == userId, JoinedAtUtc = now
            }).ToList()
        };
        db.ChatConversations.Add(duplicate);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsCharacterEncodingFailure(exception))
        { throw new InvalidOperationException(UnsupportedDatabaseEncodingMessage, exception); }
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish(members.Select(x => x.UserId));
        return duplicate.Id;
    }

    public async Task MoveChannelToCategoryEdgeAsync(long channelId, bool first, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        var channel = await RequireChannelMemberAsync(db, channelId, userId, cancellationToken);
        var siblings = await db.ChatConversations.Where(x => x.Kind == ConversationKind.Channel
            && x.ChannelGroupId == channel.ChannelGroupId).OrderBy(x => x.ChannelSortOrder).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (siblings.Count < 2 || (first ? siblings[0].Id : siblings[^1].Id) == channelId) return;
        siblings.Remove(channel);
        if (first) siblings.Insert(0, channel); else siblings.Add(channel);
        // Reuse channel slots so nested folders keep their positions among the channels.
        var slots = siblings.Select(x => x.ChannelSortOrder).Order().ToArray();
        for (var index = 0; index < siblings.Count; index++) siblings[index].ChannelSortOrder = slots[index];
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishLayoutChangedAsync(db, cancellationToken);
    }

    public async Task DeleteChannelAsync(long channelId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        var admin = await IsAdministratorAsync(db, userId, cancellationToken);
        var channel = await db.ChatConversations.SingleOrDefaultAsync(x => x.Id == channelId
            && x.Kind == ConversationKind.Channel && (admin || x.Members.Any(m => m.UserId == userId && m.IsOwner)),
            cancellationToken) ?? throw new UnauthorizedAccessException("Only channel owners or administrators can delete this channel.");
        var recipients = await db.ChatConversationMembers.Where(x => x.ConversationId == channelId)
            .Select(x => x.UserId).ToArrayAsync(cancellationToken);
        channel.DeletedAtUtc = clock.GetUtcNow().UtcDateTime;
        channel.DeletedByUserId = userId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish(recipients);
    }
}
