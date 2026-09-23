using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public static class ChatMapping
{
    public static void Configure(ModelBuilder model)
    {
        var conversation = model.Entity<Conversation>();
        conversation.ToTable("chat_conversations", t => t.HasCheckConstraint(
            "CK_chat_conversations_shape",
            "(kind = 0 AND visibility IS NULL AND name IS NULL AND direct_pair_key IS NOT NULL) OR " +
            "(kind = 1 AND visibility IS NOT NULL AND name IS NOT NULL AND btrim(name) <> '' AND direct_pair_key IS NULL)"));
        conversation.HasKey(x => x.Id);
        conversation.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        conversation.Property(x => x.Kind).HasColumnName("kind");
        conversation.Property(x => x.Visibility).HasColumnName("visibility");
        conversation.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
        conversation.Property(x => x.DirectPairKey).HasColumnName("direct_pair_key").HasMaxLength(900);
        conversation.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        conversation.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        conversation.Property(x => x.LastActivityAtUtc).HasColumnName("last_activity_at_utc");
        conversation.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        conversation.HasIndex(x => x.DirectPairKey).IsUnique();
        conversation.HasIndex(x => x.LastActivityAtUtc);

        var member = model.Entity<ConversationMember>();
        member.ToTable("chat_conversation_members");
        member.HasKey(x => new { x.ConversationId, x.UserId });
        member.Property(x => x.ConversationId).HasColumnName("conversation_id");
        member.Property(x => x.UserId).HasColumnName("user_id");
        member.Property(x => x.JoinedAtUtc).HasColumnName("joined_at_utc");
        member.Property(x => x.LastReadMessageId).HasColumnName("last_read_message_id");
        member.Property(x => x.IsOwner).HasColumnName("is_owner");
        member.HasOne(x => x.Conversation).WithMany(x => x.Members).HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        member.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        member.HasOne<ChatMessage>().WithMany()
            .HasForeignKey(x => new { x.ConversationId, x.LastReadMessageId })
            .HasPrincipalKey(x => new { x.ConversationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        member.HasIndex(x => x.UserId);

        var message = model.Entity<ChatMessage>();
        message.ToTable("chat_messages", t => t.HasCheckConstraint("CK_chat_messages_body", "char_length(body) BETWEEN 1 AND 4000"));
        message.HasKey(x => x.Id);
        message.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        message.Property(x => x.ConversationId).HasColumnName("conversation_id");
        message.Property(x => x.SenderUserId).HasColumnName("sender_user_id");
        message.Property(x => x.Type).HasColumnName("type");
        message.Property(x => x.Body).HasColumnName("body").HasMaxLength(4000);
        message.Property(x => x.SentAtUtc).HasColumnName("sent_at_utc");
        message.Property(x => x.EditedAtUtc).HasColumnName("edited_at_utc");
        message.Property(x => x.DeletedAtUtc).HasColumnName("deleted_at_utc");
        message.Property(x => x.DeletedByUserId).HasColumnName("deleted_by_user_id");
        message.HasOne(x => x.Conversation).WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        message.HasOne(x => x.SenderUser).WithMany().HasForeignKey(x => x.SenderUserId).OnDelete(DeleteBehavior.Restrict);
        message.HasOne(x => x.DeletedByUser).WithMany().HasForeignKey(x => x.DeletedByUserId).OnDelete(DeleteBehavior.Restrict);
        message.HasIndex(x => new { x.ConversationId, x.Id });
        message.HasIndex(x => x.SenderUserId);
        message.HasIndex(x => x.DeletedByUserId);
    }
}
