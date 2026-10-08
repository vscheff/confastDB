using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed partial class ChatService
{
    public static readonly IReadOnlyList<int> MuteMinutes = [15, 60, 180, 480, 1440];

    public async Task<ChatNotificationSettings> GetNotificationSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        return await LoadNotificationSettingsAsync(db, userId, cancellationToken);
    }

    private async Task<ChatNotificationSettings> LoadNotificationSettingsAsync(AppDbContext db, string userId,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var preferences = await db.Set<ChatCategoryNotificationPreference>().AsNoTracking()
            .Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var categories = preferences.ToDictionary(x => x.GroupId,
            x => new ChatNotificationSetting(x.Mode, x.Mode, ActiveMute(x.IsMuted, x.MutedUntilUtc, now), x.MutedUntilUtc));
        var members = await db.ChatConversationMembers.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new { x.ConversationId, x.NotificationMode, x.IsMuted, x.MutedUntilUtc, x.Conversation.ChannelGroupId })
            .ToListAsync(cancellationToken);
        var channels = members.ToDictionary(x => x.ConversationId, x => new ChatNotificationSetting(x.NotificationMode,
            x.NotificationMode != ChatNotificationMode.CategoryDefault ? x.NotificationMode
                : x.ChannelGroupId is long groupId && categories.TryGetValue(groupId, out var category)
                    ? category.Mode : ChatNotificationMode.AllMessages,
            ActiveMute(x.IsMuted, x.MutedUntilUtc, now), x.MutedUntilUtc));
        return new(channels, categories);
    }

    private static bool ActiveMute(bool muted, DateTime? until, DateTime now) => muted && (until == null || until > now);

    private DateTime? MuteDeadline(bool muted, int? minutes)
    {
        if (minutes is int duration && (!muted || !MuteMinutes.Contains(duration)))
            throw new InvalidOperationException("Choose a supported mute duration.");
        return muted && minutes is int value ? clock.GetUtcNow().UtcDateTime.AddMinutes(value) : null;
    }

    public async Task SetChannelNotificationModeAsync(long channelId, ChatNotificationMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new InvalidOperationException("Choose a notification setting.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await RequireNotificationChannelAsync(db, channelId, userId, cancellationToken);
        await db.ChatConversationMembers.Where(x => x.ConversationId == channelId && x.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NotificationMode, mode), cancellationToken);
        notifications.Publish([userId]);
    }

    public async Task SetChannelMutedAsync(long channelId, bool muted, int? minutes = null,
        CancellationToken cancellationToken = default)
    {
        var until = MuteDeadline(muted, minutes);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await RequireNotificationChannelAsync(db, channelId, userId, cancellationToken);
        await db.ChatConversationMembers.Where(x => x.ConversationId == channelId && x.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsMuted, muted).SetProperty(x => x.MutedUntilUtc, until), cancellationToken);
        notifications.Publish([userId]);
    }

    private static async Task RequireNotificationChannelAsync(AppDbContext db, long channelId, string userId,
        CancellationToken cancellationToken)
    {
        if (!await db.ChatConversationMembers.AnyAsync(x => x.ConversationId == channelId && x.UserId == userId
            && x.Conversation.Kind == ConversationKind.Channel, cancellationToken))
            throw new UnauthorizedAccessException("You do not belong to this channel.");
    }

    private static async Task RequireVisibleNotificationCategoryAsync(AppDbContext db, long groupId, string userId,
        CancellationToken cancellationToken)
    {
        var access = await LoadGroupAccessAsync(db, userId, cancellationToken);
        if (!access.TryGetValue(groupId, out var group) || !group.Visible)
            throw new UnauthorizedAccessException("You cannot access this category.");
    }

    public async Task SetCategoryNotificationModeAsync(long groupId, ChatNotificationMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode) || mode == ChatNotificationMode.CategoryDefault)
            throw new InvalidOperationException("Choose a category notification setting.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        await RequireVisibleNotificationCategoryAsync(db, groupId, userId, cancellationToken);
        // Update only the chosen field so concurrent mute changes are preserved.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_category_notification_preferences (user_id, group_id, mode, is_muted)
            VALUES ({userId}, {groupId}, {(int)mode}, FALSE)
            ON CONFLICT (user_id, group_id) DO UPDATE SET mode = EXCLUDED.mode
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish([userId]);
    }

    public async Task SetCategoryMutedAsync(long groupId, bool muted, int? minutes = null,
        CancellationToken cancellationToken = default)
    {
        var until = MuteDeadline(muted, minutes);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChannelLayoutAsync(db, cancellationToken);
        await RequireVisibleNotificationCategoryAsync(db, groupId, userId, cancellationToken);
        var groups = await db.ChatChannelGroups.Select(x => new { x.Id, x.ParentGroupId }).ToListAsync(cancellationToken);
        var descendants = new HashSet<long> { groupId };
        while (true)
        {
            var added = groups.Where(x => x.ParentGroupId is long parent && descendants.Contains(parent))
                .Select(x => x.Id).Where(x => !descendants.Contains(x)).ToArray();
            if (added.Length == 0) break;
            descendants.UnionWith(added);
        }
        await db.ChatConversationMembers.Where(x => x.UserId == userId && x.Conversation.ChannelGroupId != null
            && descendants.Contains(x.Conversation.ChannelGroupId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsMuted, muted).SetProperty(x => x.MutedUntilUtc, until), cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_category_notification_preferences (user_id, group_id, mode, is_muted, muted_until_utc)
            VALUES ({userId}, {groupId}, 1, {muted}, {until})
            ON CONFLICT (user_id, group_id) DO UPDATE SET is_muted = EXCLUDED.is_muted, muted_until_utc = EXCLUDED.muted_until_utc
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        notifications.Publish([userId]);
    }

    public async Task<IReadOnlyDictionary<long, ChatNotificationUnreadSummary>> GetNotificationUnreadCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var settings = await LoadNotificationSettingsAsync(db, userId, cancellationToken);
        var rows = await db.ChatConversationMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Select(m => new
            {
                m.ConversationId, m.Conversation.Kind, m.IsManuallyUnread,
                Count = db.ChatMessages.Count(x => x.ConversationId == m.ConversationId
                    && x.Id > (m.LastReadMessageId ?? 0) && x.SenderUserId != userId && x.DeletedAtUtc == null
                    && !x.Reads.Any(r => r.UserId == userId) && (x.ChannelThreadId == null || x.Type == ChatMessageType.ThreadNotice)),
                Mentions = db.ChatMessages.Count(x => x.ConversationId == m.ConversationId
                    && x.Id > (m.LastReadMessageId ?? 0) && x.SenderUserId != userId && x.DeletedAtUtc == null
                    && !x.Reads.Any(r => r.UserId == userId) && x.ChannelThreadId == null
                    && (x.Mentions.Any(mention => mention.UserId == userId)
                        || (x.ReplyToMessage != null && x.ReplyToMessage.SenderUserId == userId)))
            }).ToListAsync(cancellationToken);
        var counts = new Dictionary<long, ChatNotificationUnreadSummary>();
        foreach (var row in rows)
        {
            var setting = settings.Channels[row.ConversationId];
            var direct = row.Kind != ConversationKind.Channel;
            var allowed = direct || setting.AllowsToast(false);
            var unread = allowed ? Math.Max(row.IsManuallyUnread ? 1 : 0, row.Count)
                : setting.AllowsToast(true) ? row.Mentions : 0;
            counts[row.ConversationId] = new(unread, unread > 0 && (direct || row.Mentions > 0));
        }
        return counts;
    }
    public async Task<ChatNotificationUnreadSummary> GetNotificationUnreadSummaryAsync(CancellationToken cancellationToken = default)
    {
        var counts = await GetNotificationUnreadCountsAsync(cancellationToken);
        return new(counts.Values.Sum(x => x.Count), counts.Values.Any(x => x.HasAlert));
    }

}
