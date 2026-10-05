using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed class ChatScheduledMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public string SenderUserId { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public long? ChannelThreadId { get; set; }
    public long? ReplyToMessageId { get; set; }
    public DateTime ScheduledAtUtc { get; set; }
    public string? Failure { get; set; }
    public List<ChatScheduledAttachment> Attachments { get; set; } = [];
}

public sealed class ChatScheduledAttachment
{
    public long Id { get; set; }
    public long ScheduledMessageId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
}

public static class ChatScheduledMessageMapping
{
    public static void Configure(ModelBuilder model)
    {
        var message = model.Entity<ChatScheduledMessage>();
        message.ToTable("chat_scheduled_messages", t => t.HasCheckConstraint("CK_chat_scheduled_messages_body", "char_length(body) BETWEEN 1 AND 4000"));
        message.HasKey(x => x.Id);
        message.Property(x => x.Id).HasColumnName("id");
        message.Property(x => x.ConversationId).HasColumnName("conversation_id");
        message.Property(x => x.SenderUserId).HasColumnName("sender_user_id");
        message.Property(x => x.Body).HasColumnName("body").HasMaxLength(4000);
        message.Property(x => x.ChannelThreadId).HasColumnName("channel_thread_id");
        message.Property(x => x.ReplyToMessageId).HasColumnName("reply_to_message_id");
        message.Property(x => x.ScheduledAtUtc).HasColumnName("scheduled_at_utc");
        message.Property(x => x.Failure).HasColumnName("failure").HasMaxLength(500);
        message.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        message.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.SenderUserId).OnDelete(DeleteBehavior.Restrict);
        message.HasOne<ChatChannelThread>().WithMany().HasForeignKey(x => new { x.ConversationId, x.ChannelThreadId })
            .HasPrincipalKey(x => new { x.ConversationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        message.HasOne<ChatMessage>().WithMany().HasForeignKey(x => new { x.ConversationId, x.ReplyToMessageId })
            .HasPrincipalKey(x => new { x.ConversationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        message.HasIndex(x => x.ScheduledAtUtc).HasFilter("failure IS NULL");

        var attachment = model.Entity<ChatScheduledAttachment>();
        attachment.ToTable("chat_scheduled_attachments", t =>
        {
            t.HasCheckConstraint("CK_chat_scheduled_attachments_content", "octet_length(content) BETWEEN 1 AND 26214400");
            t.HasCheckConstraint("CK_chat_scheduled_attachments_name", "btrim(file_name) <> ''");
        });
        attachment.HasKey(x => x.Id);
        attachment.Property(x => x.Id).HasColumnName("id");
        attachment.Property(x => x.ScheduledMessageId).HasColumnName("scheduled_message_id");
        attachment.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255);
        attachment.Property(x => x.Content).HasColumnName("content");
        attachment.HasOne<ChatScheduledMessage>().WithMany(x => x.Attachments)
            .HasForeignKey(x => x.ScheduledMessageId).OnDelete(DeleteBehavior.Cascade);
    }
}
