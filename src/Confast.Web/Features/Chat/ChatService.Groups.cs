using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed record ChatGroupIconUpload(byte[]? PictureData = null, string? GiphyId = null);
public sealed record ChatGroupStartResult(long? CreatedConversationId, IReadOnlyList<ChatConversationRow> ExistingConversations);

public sealed partial class ChatService
{
    public async Task HideDirectConversationAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize hiding with message delivery, so a newer incoming message always restores visibility.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM chat_conversations WHERE id = {conversationId} FOR UPDATE", cancellationToken);
        var updated = await db.ChatConversationMembers.Where(x => x.ConversationId == conversationId && x.UserId == userId
                && x.Conversation.Kind == ConversationKind.Direct)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, true), cancellationToken);
        if (updated == 0) throw new UnauthorizedAccessException("You do not belong to this direct conversation.");
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish([userId]);
    }

    private static Task<int> RestoreDirectVisibilityAsync(Confast.Web.Data.AppDbContext db, long conversationId,
        CancellationToken cancellationToken) =>
        db.ChatConversationMembers.Where(x => x.ConversationId == conversationId && x.IsHidden
                && x.Conversation.Kind == ConversationKind.Direct)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsHidden, false), cancellationToken);

    public async Task SetConversationPinnedToTopAsync(long conversationId, bool pinned,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var updated = await db.ChatConversationMembers.Where(x => x.ConversationId == conversationId && x.UserId == userId
                && x.Conversation.Kind != ConversationKind.Channel)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PinnedToTopAtUtc,
                x => pinned ? x.PinnedToTopAtUtc ?? now : null), cancellationToken);
        if (updated == 0) throw new UnauthorizedAccessException("You do not belong to this conversation.");
        notifications.Publish([userId]);
    }

    public async Task SetConversationMutedAsync(long conversationId, bool muted, int? minutes = null,
        CancellationToken cancellationToken = default)
    {
        var until = MuteDeadline(muted, minutes);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var updated = await db.ChatConversationMembers.Where(x => x.ConversationId == conversationId && x.UserId == userId
                && x.Conversation.Kind != ConversationKind.Channel)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsMuted, muted)
                .SetProperty(x => x.MutedUntilUtc, until), cancellationToken);
        if (updated == 0) throw new UnauthorizedAccessException("You do not belong to this conversation.");
        notifications.Publish([userId]);
    }

    public async Task LeaveGroupAsync(long conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Use the same lock as additions, so joining and leaving cannot race over the member set.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM chat_conversations WHERE id = {conversationId} FOR UPDATE", cancellationToken);
        var group = await db.ChatConversations.Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == conversationId && x.Kind == ConversationKind.Group
                && x.Members.Any(m => m.UserId == userId), cancellationToken)
            ?? throw new UnauthorizedAccessException("You do not belong to this group chat.");
        var recipients = group.Members.Select(x => x.UserId).ToArray();
        db.ChatConversationMembers.Remove(group.Members.Single(x => x.UserId == userId));
        var now = clock.GetUtcNow().UtcDateTime;
        db.ChatMessages.Add(new ChatMessage
        {
            ConversationId = conversationId, SenderUserId = userId, Type = ChatMessageType.MemberLeftNotice,
            Body = "left the group.", SentAtUtc = now
        });
        group.LastActivityAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish(recipients);
    }

    public async Task<long> AddConversationMembersAsync(long conversationId, IEnumerable<string> memberUserIds,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize membership additions so overlapping selections create each membership and event once.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM chat_conversations WHERE id = {conversationId} FOR UPDATE", cancellationToken);
        var source = await db.ChatConversations.Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == conversationId && x.Members.Any(m => m.UserId == userId), cancellationToken)
            ?? throw new UnauthorizedAccessException("You do not belong to this conversation.");
        if (source.Kind is not (ConversationKind.Direct or ConversationKind.Group))
            throw new InvalidOperationException("Use channel settings to manage channel members.");

        var existingIds = source.Members.Select(x => x.UserId).ToHashSet(StringComparer.Ordinal);
        var addedIds = memberUserIds.Distinct(StringComparer.Ordinal).Where(id => !existingIds.Contains(id)).ToArray();
        if (addedIds.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return source.Id;
        }
        var addedUsers = await db.Users.Where(x => addedIds.Contains(x.Id) && x.IsActive)
            .OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
            .Select(x => new { x.Id, Name = x.DisplayName ?? x.UserName ?? "User" }).ToArrayAsync(cancellationToken);
        if (addedUsers.Length != addedIds.Length)
            throw new InvalidOperationException("Select active users to add to the conversation.");
        var now = clock.GetUtcNow().UtcDateTime;
        var destination = source;
        if (source.Kind == ConversationKind.Direct)
        {
            if (existingIds.Count + addedIds.Length < 3)
                throw new InvalidOperationException("A group chat needs at least three people, including you.");
            // The direct chat and its private history stay intact; only its participants carry forward.
            destination = new Conversation
            {
                Kind = ConversationKind.Group, CreatedByUserId = userId,
                CreatedAtUtc = now, LastActivityAtUtc = now,
                Members = existingIds.Select(id => new ConversationMember { UserId = id, JoinedAtUtc = now }).ToList()
            };
            db.ChatConversations.Add(destination);
        }
        foreach (var added in addedUsers)
        {
            destination.Members.Add(new ConversationMember { UserId = added.Id, JoinedAtUtc = now });
            db.ChatMessages.Add(new ChatMessage
            {
                Conversation = destination, SenderUserId = userId, Type = ChatMessageType.MemberAddedNotice,
                Body = $"added {added.Name} to the conversation.", SentAtUtc = now,
                Tags = [new ChatMessageTag { Start = 6, Length = added.Name.Length,
                    Kind = ChatMentionKind.User, UserId = added.Id }]
            });
        }
        destination.LastActivityAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish(existingIds.Concat(addedIds));
        return destination.Id;
    }

    public async Task<long> CreateGroupAsync(IEnumerable<string> memberUserIds,
        CancellationToken cancellationToken = default) =>
        (await StartGroupAsync(memberUserIds, createAnother: true, cancellationToken: cancellationToken)).CreatedConversationId!.Value;

    public async Task<ChatGroupStartResult> StartGroupAsync(IEnumerable<string> memberUserIds,
        string? name = null, ChatGroupIconUpload? icon = null, bool createAnother = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var ids = memberUserIds.Append(userId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ids.Length < 3)
            throw new InvalidOperationException("A group chat needs at least three people, including you.");
        if (await db.Users.CountAsync(x => ids.Contains(x.Id) && x.IsActive, cancellationToken) != ids.Length)
            throw new InvalidOperationException("Select active users for the group chat.");
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name?.Length > 120)
            throw new InvalidOperationException("Group chat name must be 120 characters or fewer.");
        var (picture, contentType) = ValidateGroupIcon(icon);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize the exact member set so simultaneous creates also see the first committed chat.
        var memberKey = string.Join("|", ids.Select(id => $"{id.Length}:{id}"));
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({memberKey}, 0))", cancellationToken);
        if (!createAnother)
        {
            var existingIds = await db.ChatConversations.AsNoTracking()
                .Where(x => x.Kind == ConversationKind.Group && x.Members.Count == ids.Length
                    && x.Members.All(member => ids.Contains(member.UserId)))
                .Select(x => x.Id).ToArrayAsync(cancellationToken);
            if (existingIds.Length > 0)
            {
                var existing = (await GetConversationsAsync(cancellationToken))
                    .Where(x => existingIds.Contains(x.Id)).ToArray();
                await transaction.CommitAsync(cancellationToken);
                return new(null, existing);
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var conversation = new Conversation
        {
            Kind = ConversationKind.Group,
            Name = name, IconData = picture, IconContentType = contentType, IconGiphyId = icon?.GiphyId,
            CreatedByUserId = userId, CreatedAtUtc = now, LastActivityAtUtc = now,
            Members = ids.Select(id => new ConversationMember { UserId = id, JoinedAtUtc = now }).ToList()
        };
        db.ChatConversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish(ids);
        return new(conversation.Id, []);
    }

    private static (byte[]? Picture, string? ContentType) ValidateGroupIcon(ChatGroupIconUpload? icon)
    {
        var picture = icon?.PictureData?.ToArray();
        string? contentType = null;
        if (picture is not null)
        {
            if (picture.Length is 0 or > UserProfilePictureService.MaximumPictureBytes)
                throw new InvalidOperationException("Choose an icon picture smaller than 1 MB.");
            contentType = UserProfilePictureService.DetectContentType(picture)
                ?? throw new InvalidOperationException("Choose a PNG, JPEG, GIF, or WebP icon picture.");
            if (icon?.GiphyId is not null)
                throw new InvalidOperationException("Choose either an uploaded picture or a GIF for the icon.");
        }
        if (icon?.GiphyId is { } giphyId) ValidateGiphyId(giphyId);

        return (picture, contentType);
    }

    public async Task<(byte[] Data, string ContentType)?> GetGroupIconForHttpUserAsync(long conversationId,
        string requesterUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId
                && x.UserId == requesterUserId && x.User.IsActive && x.Conversation.Kind == ConversationKind.Group,
                cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this group chat.");
        var picture = await db.ChatConversations.AsNoTracking().Where(x => x.Id == conversationId && x.IconData != null)
            .Select(x => new { x.IconData, x.IconContentType }).SingleOrDefaultAsync(cancellationToken);
        return picture is null ? null : (picture.IconData!, picture.IconContentType!);
    }

    public Task RenameGroupAsync(long conversationId, string name, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(conversationId, name, changeIcon: false, cancellationToken: cancellationToken);

    public async Task UpdateGroupAsync(long conversationId, string? name, ChatGroupIconUpload? icon = null,
        bool changeIcon = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == conversationId
                && x.UserId == userId && x.Conversation.Kind == ConversationKind.Group, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this group chat.");
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name?.Length > 120)
            throw new InvalidOperationException("Group chat name must be 120 characters or fewer.");
        // A null name uses current member names; never truncate that list to fit the custom-name column.
        var query = db.ChatConversations.Where(x => x.Id == conversationId && x.Kind == ConversationKind.Group
            && x.Members.Any(m => m.UserId == userId));
        if (changeIcon)
        {
            var (picture, contentType) = ValidateGroupIcon(icon);
            await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, name)
                .SetProperty(x => x.IconData, picture).SetProperty(x => x.IconContentType, contentType)
                .SetProperty(x => x.IconGiphyId, icon == null ? null : icon.GiphyId), cancellationToken);
        }
        else await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, name), cancellationToken);
        notifications.Publish(await db.ChatConversationMembers.Where(x => x.ConversationId == conversationId)
            .Select(x => x.UserId).ToListAsync(cancellationToken));
    }
}
