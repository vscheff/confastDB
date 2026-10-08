using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed record ChatGroupIconUpload(byte[]? PictureData = null, string? GiphyId = null);
public sealed record ChatGroupStartResult(long? CreatedConversationId, IReadOnlyList<ChatConversationRow> ExistingConversations);

public sealed partial class ChatService
{
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
