using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed class ChatModerationHistory
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public long ConversationId { get; set; }
    public long? ChannelThreadId { get; set; }
    public string ModeratorUserId { get; set; } = string.Empty;
    public string OriginalSenderUserId { get; set; } = string.Empty;
    public DateTime DeletedAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public bool WasPinned { get; set; }
}

internal static class ChatModerationMapping
{
    internal static void Configure(ModelBuilder model)
    {
        var history = model.Entity<ChatModerationHistory>();
        history.ToTable("chat_moderation_history", t =>
        {
            t.HasCheckConstraint("CK_chat_moderation_reason", "btrim(reason) <> ''");
            t.HasCheckConstraint("CK_chat_moderation_other_sender", "moderator_user_id <> original_sender_user_id");
        });
        history.HasKey(x => x.Id);
        history.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        history.Property(x => x.MessageId).HasColumnName("message_id");
        history.Property(x => x.ConversationId).HasColumnName("conversation_id");
        history.Property(x => x.ChannelThreadId).HasColumnName("channel_thread_id");
        history.Property(x => x.ModeratorUserId).HasColumnName("moderator_user_id");
        history.Property(x => x.OriginalSenderUserId).HasColumnName("original_sender_user_id");
        history.Property(x => x.DeletedAtUtc).HasColumnName("deleted_at_utc");
        history.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1000);
        history.Property(x => x.OperationId).HasColumnName("operation_id");
        history.Property(x => x.WasPinned).HasColumnName("was_pinned");
        history.HasIndex(x => x.MessageId).IsUnique();
        history.HasIndex(x => new { x.ConversationId, x.Id });
        history.HasOne<ChatMessage>().WithMany().HasForeignKey(x => new { x.ConversationId, x.MessageId })
            .HasPrincipalKey(x => new { x.ConversationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        history.HasOne<ChatChannelThread>().WithMany().HasForeignKey(x => new { x.ConversationId, x.ChannelThreadId })
            .HasPrincipalKey(x => new { x.ConversationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        history.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ModeratorUserId).OnDelete(DeleteBehavior.Restrict);
        history.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OriginalSenderUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
