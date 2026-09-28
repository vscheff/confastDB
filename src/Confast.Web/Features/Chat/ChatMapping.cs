using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public static class ChatMapping
{
    public static void Configure(ModelBuilder model)
    {
        var group = model.Entity<ChatChannelGroup>();
        group.ToTable("chat_channel_groups", t =>
        {
            t.HasCheckConstraint("CK_chat_channel_groups_name", "btrim(name) <> ''");
            t.HasCheckConstraint("CK_chat_channel_groups_parent", "parent_group_id IS NULL OR parent_group_id <> id");
        });
        group.HasKey(x => x.Id);
        group.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        group.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
        group.Property(x => x.ParentGroupId).HasColumnName("parent_group_id");
        group.Property(x => x.SortOrder).HasColumnName("sort_order");
        group.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        group.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        group.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        group.HasOne(x => x.ParentGroup).WithMany(x => x.ChildGroups).HasForeignKey(x => x.ParentGroupId).OnDelete(DeleteBehavior.Restrict);
        group.HasIndex(x => new { x.SortOrder, x.Id });
        group.HasIndex(x => new { x.ParentGroupId, x.SortOrder, x.Id });
        group.HasIndex(x => x.Name);

        var conversation = model.Entity<Conversation>();
        conversation.ToTable("chat_conversations", t => t.HasCheckConstraint(
            "CK_chat_conversations_shape",
            "(kind = 0 AND visibility IS NULL AND name IS NULL AND direct_pair_key IS NOT NULL AND channel_group_id IS NULL AND channel_sort_order = 0) OR " +
            "(kind = 1 AND visibility IS NOT NULL AND name IS NOT NULL AND btrim(name) <> '' AND direct_pair_key IS NULL AND channel_sort_order >= 0)"));
        conversation.HasKey(x => x.Id);
        conversation.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        conversation.Property(x => x.Kind).HasColumnName("kind");
        conversation.Property(x => x.Visibility).HasColumnName("visibility");
        conversation.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
        conversation.Property(x => x.ChannelGroupId).HasColumnName("channel_group_id");
        conversation.Property(x => x.ChannelSortOrder).HasColumnName("channel_sort_order");
        conversation.Property(x => x.DirectPairKey).HasColumnName("direct_pair_key").HasMaxLength(900);
        conversation.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        conversation.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        conversation.Property(x => x.LastActivityAtUtc).HasColumnName("last_activity_at_utc");
        conversation.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        conversation.HasOne(x => x.ChannelGroup).WithMany(x => x.Channels).HasForeignKey(x => x.ChannelGroupId).OnDelete(DeleteBehavior.SetNull);
        conversation.HasIndex(x => x.DirectPairKey).IsUnique();
        conversation.HasIndex(x => x.LastActivityAtUtc);
        conversation.HasIndex(x => new { x.ChannelGroupId, x.ChannelSortOrder, x.Id });

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

        var mention = model.Entity<ChatMessageMention>();
        mention.ToTable("chat_message_mentions");
        mention.HasKey(x => new { x.MessageId, x.UserId });
        mention.Property(x => x.MessageId).HasColumnName("message_id");
        mention.Property(x => x.UserId).HasColumnName("user_id");
        mention.HasOne(x => x.Message).WithMany(x => x.Mentions).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        mention.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        mention.HasIndex(x => x.UserId);

        var tag = model.Entity<ChatMessageTag>();
        tag.ToTable("chat_message_tags", table => table.HasCheckConstraint("CK_chat_message_tags_shape",
            "start >= 0 AND length > 0 AND ((kind = 0 AND user_id IS NOT NULL) OR (kind IN (1, 2) AND user_id IS NULL))"));
        tag.HasKey(x => new { x.MessageId, x.Start });
        tag.Property(x => x.MessageId).HasColumnName("message_id");
        tag.Property(x => x.Start).HasColumnName("start");
        tag.Property(x => x.Length).HasColumnName("length");
        tag.Property(x => x.Kind).HasColumnName("kind");
        tag.Property(x => x.UserId).HasColumnName("user_id");
        tag.HasOne(x => x.Message).WithMany(x => x.Tags).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        tag.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        tag.HasIndex(x => x.UserId);

        var reaction = model.Entity<ChatMessageReaction>();
        reaction.ToTable("chat_message_reactions");
        reaction.HasKey(x => new { x.MessageId, x.UserId, x.Emoji });
        reaction.Property(x => x.MessageId).HasColumnName("message_id");
        reaction.Property(x => x.UserId).HasColumnName("user_id");
        reaction.Property(x => x.Emoji).HasColumnName("emoji").HasMaxLength(32);
        reaction.Property(x => x.ReactedAtUtc).HasColumnName("reacted_at_utc");
        reaction.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        reaction.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        reaction.HasIndex(x => x.UserId);

        var tonePreference = model.Entity<ChatEmojiTonePreference>();
        tonePreference.ToTable("chat_emoji_tone_preferences");
        tonePreference.HasKey(x => new { x.UserId, x.DefaultEmoji });
        tonePreference.Property(x => x.UserId).HasColumnName("user_id");
        tonePreference.Property(x => x.DefaultEmoji).HasColumnName("default_emoji").HasMaxLength(32);
        tonePreference.Property(x => x.PreferredEmoji).HasColumnName("preferred_emoji").HasMaxLength(32);
        tonePreference.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        var presenceSession = model.Entity<UserPresenceSession>();
        presenceSession.ToTable("chat_presence_sessions");
        presenceSession.HasKey(x => x.Id);
        presenceSession.Property(x => x.Id).HasColumnName("id");
        presenceSession.Property(x => x.UserId).HasColumnName("user_id");
        presenceSession.Property(x => x.LastHeartbeatAtUtc).HasColumnName("last_heartbeat_at_utc");
        presenceSession.Property(x => x.LastActivityAtUtc).HasColumnName("last_activity_at_utc");
        presenceSession.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        presenceSession.HasIndex(x => new { x.UserId, x.LastHeartbeatAtUtc });

    }
}
