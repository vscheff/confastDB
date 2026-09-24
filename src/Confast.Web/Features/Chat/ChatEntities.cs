using Confast.Web.Features.Identity;

namespace Confast.Web.Features.Chat;

public enum ConversationKind { Direct, Channel }
public enum ChannelVisibility { Public, Private }
public enum ChatMessageType { Text }

public sealed class ChatChannelGroup
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public List<Conversation> Channels { get; set; } = [];
}

public sealed class Conversation
{
    public long Id { get; set; }
    public ConversationKind Kind { get; set; }
    public ChannelVisibility? Visibility { get; set; }
    public string? Name { get; set; }
    public long? ChannelGroupId { get; set; }
    public ChatChannelGroup? ChannelGroup { get; set; }
    public int ChannelSortOrder { get; set; }
    // Sorted Identity IDs make the pair unique even under concurrent creation.
    public string? DirectPairKey { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastActivityAtUtc { get; set; }
    public List<ConversationMember> Members { get; set; } = [];
}

public sealed class ConversationMember
{
    public long ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public DateTime JoinedAtUtc { get; set; }
    public long? LastReadMessageId { get; set; }
    public bool IsOwner { get; set; }
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    // Null is reserved for future system/agent senders. Only human sends exist today.
    public string? SenderUserId { get; set; }
    public ApplicationUser? SenderUser { get; set; }
    public ChatMessageType Type { get; set; } = ChatMessageType.Text;
    public string Body { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; }
    public DateTime? EditedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedByUserId { get; set; }
    public ApplicationUser? DeletedByUser { get; set; }
}

public sealed class ChatMessageReaction
{
    public long MessageId { get; set; }
    public ChatMessage Message { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public string Emoji { get; set; } = string.Empty;
    public DateTime ReactedAtUtc { get; set; }
}

public sealed class ChatEmojiTonePreference
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public string DefaultEmoji { get; set; } = string.Empty;
    public string PreferredEmoji { get; set; } = string.Empty;
}
