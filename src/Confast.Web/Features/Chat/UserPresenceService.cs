using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed class UserPresenceService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICurrentUser currentUser,
    ChatNotifications notifications,
    TimeProvider clock)
{
    public static readonly TimeSpan HeartbeatLifetime = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);

    private async Task<string> RequireUserAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = await currentUser.GetUserIdAsync();
        if (userId is null || !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken))
            throw new UnauthorizedAccessException("An active account is required for presence.");
        return userId;
    }

    public async Task<IReadOnlyDictionary<string, UserPresence>> GetVisibleAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var viewerId = await RequireUserAsync(db, cancellationToken);
        var users = await db.Users.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new { x.Id, x.PresencePreference, x.StatusEmoji, x.StatusMessage })
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now - HeartbeatLifetime;
        var activity = await db.UserPresenceSessions.AsNoTracking()
            .Where(x => x.LastHeartbeatAtUtc >= cutoff && x.User.IsActive)
            .GroupBy(x => x.UserId)
            .Select(x => new { UserId = x.Key, LastActivityAtUtc = x.Max(s => s.LastActivityAtUtc) })
            .ToDictionaryAsync(x => x.UserId, x => x.LastActivityAtUtc, cancellationToken);

        return users.ToDictionary(x => x.Id, x =>
        {
            var isSelf = x.Id == viewerId;
            var state = ResolveState(x.PresencePreference, activity.GetValueOrDefault(x.Id), now, isSelf);
            var hideStatus = !isSelf && x.PresencePreference == UserPresencePreference.Invisible;
            return new UserPresence(x.Id, state,
                isSelf ? x.PresencePreference : UserPresencePreference.Online,
                hideStatus ? null : x.StatusEmoji,
                hideStatus ? null : x.StatusMessage);
        });
    }

    public async Task<UserPresence> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var userId = await currentUser.GetUserIdAsync()
            ?? throw new UnauthorizedAccessException("Sign in to view your presence.");
        var visible = await GetVisibleAsync(cancellationToken);
        return visible[userId];
    }

    public static UserPresenceState ResolveState(UserPresencePreference preference,
        DateTime lastActivityAtUtc, DateTime now, bool isSelf)
    {
        if (lastActivityAtUtc == default) return UserPresenceState.Offline;
        if (preference == UserPresencePreference.Invisible)
            return isSelf ? UserPresenceState.Invisible : UserPresenceState.Offline;
        if (preference == UserPresencePreference.DoNotDisturb)
            return UserPresenceState.DoNotDisturb;
        if (preference == UserPresencePreference.Idle)
            return UserPresenceState.Idle;
        if (now - lastActivityAtUtc >= IdleAfter)
            return UserPresenceState.Idle;
        return UserPresenceState.Online;
    }

    public async Task SetPreferenceAsync(UserPresencePreference preference,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(preference)) throw new InvalidOperationException("Choose a valid presence state.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await db.Users.Where(x => x.Id == userId)
            .ExecuteUpdateAsync(x => x.SetProperty(user => user.PresencePreference, preference), cancellationToken);
        await NotifyUsersAsync(db, cancellationToken);
    }

    public async Task SetCustomStatusAsync(string? emoji, string? message,
        CancellationToken cancellationToken = default)
    {
        emoji = string.IsNullOrWhiteSpace(emoji) ? null : emoji.Trim();
        message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (message?.Length > 140) throw new InvalidOperationException("Status message must be 140 characters or fewer.");
        if (emoji is not null && ChatEmojiCatalog.FindFamilyForEmoji(emoji) is null)
            throw new InvalidOperationException("Choose an emoji from the picker.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        await db.Users.Where(x => x.Id == userId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(user => user.StatusEmoji, emoji)
                .SetProperty(user => user.StatusMessage, message), cancellationToken);
        await NotifyUsersAsync(db, cancellationToken);
    }

    public async Task HeartbeatAsync(Guid sessionId, double idleSeconds,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new InvalidOperationException("A presence session is required.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireUserAsync(db, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var boundedIdle = double.IsFinite(idleSeconds) ? Math.Clamp(idleSeconds, 0, 86400) : 86400;
        var lastActivity = now - TimeSpan.FromSeconds(boundedIdle);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_presence_sessions (id, user_id, last_heartbeat_at_utc, last_activity_at_utc)
            VALUES ({sessionId}, {userId}, {now}, {lastActivity})
            ON CONFLICT (id) DO UPDATE SET
                last_heartbeat_at_utc = EXCLUDED.last_heartbeat_at_utc,
                last_activity_at_utc = EXCLUDED.last_activity_at_utc
            WHERE chat_presence_sessions.user_id = EXCLUDED.user_id
            """, cancellationToken);
        await db.UserPresenceSessions.Where(x => x.UserId == userId && x.LastHeartbeatAtUtc < now.AddDays(-1))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await currentUser.GetUserIdAsync();
        if (userId is null) return;
        await db.UserPresenceSessions.Where(x => x.Id == sessionId && x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task NotifyUsersAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var ids = await db.Users.AsNoTracking().Where(x => x.IsActive)
            .Select(x => x.Id).ToArrayAsync(cancellationToken);
        notifications.Publish(ids);
    }
}
