using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public enum ChatNotificationMode { CategoryDefault, AllMessages, MentionsAndUnreads, OnlyMentions, Nothing }

public sealed class ChatCategoryNotificationPreference
{
    public string UserId { get; set; } = string.Empty;
    public long GroupId { get; set; }
    public ChatNotificationMode Mode { get; set; } = ChatNotificationMode.AllMessages;
    public bool IsMuted { get; set; }
    public DateTime? MutedUntilUtc { get; set; }
}

public sealed record ChatNotificationSetting(ChatNotificationMode Mode, ChatNotificationMode EffectiveMode,
    bool IsMuted, DateTime? MutedUntilUtc)
{
    public bool AllowsPopup(bool mentioned) => !IsMuted && (EffectiveMode == ChatNotificationMode.AllMessages
        || (mentioned && EffectiveMode is ChatNotificationMode.MentionsAndUnreads or ChatNotificationMode.OnlyMentions));
    public bool AllowsToast(bool mentioned) => !IsMuted && (EffectiveMode is ChatNotificationMode.AllMessages
        or ChatNotificationMode.MentionsAndUnreads || (mentioned && EffectiveMode == ChatNotificationMode.OnlyMentions));
}

public sealed record ChatNotificationSettings(IReadOnlyDictionary<long, ChatNotificationSetting> Channels,
    IReadOnlyDictionary<long, ChatNotificationSetting> Categories);
public sealed record ChatNotificationUnreadSummary(int Count, bool HasAlert);

public static class ChatNotificationMapping
{
    public static void Configure(ModelBuilder model)
    {
        var preference = model.Entity<ChatCategoryNotificationPreference>();
        preference.ToTable("chat_category_notification_preferences", t =>
        {
            t.HasCheckConstraint("CK_chat_category_notifications_mode", "mode BETWEEN 1 AND 4");
            t.HasCheckConstraint("CK_chat_category_notifications_mute", "is_muted OR muted_until_utc IS NULL");
        });
        preference.HasKey(x => new { x.UserId, x.GroupId });
        preference.Property(x => x.UserId).HasColumnName("user_id");
        preference.Property(x => x.GroupId).HasColumnName("group_id");
        preference.Property(x => x.Mode).HasColumnName("mode");
        preference.Property(x => x.IsMuted).HasColumnName("is_muted");
        preference.Property(x => x.MutedUntilUtc).HasColumnName("muted_until_utc");
        preference.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        preference.HasOne<ChatChannelGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
    }
}
