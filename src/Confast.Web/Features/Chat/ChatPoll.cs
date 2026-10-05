using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed class ChatPoll
{
    public long MessageId { get; set; }
    public ChatMessage Message { get; set; } = null!;
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public bool AllowMultipleAnswers { get; set; }
    public List<ChatPollAnswer> Answers { get; set; } = [];
}

public sealed class ChatPollAnswer
{
    public long Id { get; set; }
    public long PollMessageId { get; set; }
    public ChatPoll Poll { get; set; } = null!;
    public int Position { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? Emoji { get; set; }
    public List<ChatPollVote> Votes { get; set; } = [];
}

public sealed class ChatPollVote
{
    public long PollMessageId { get; set; }
    public long AnswerId { get; set; }
    public ChatPollAnswer Answer { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
}

public sealed record ChatPollAnswerInput(string Text, string? Emoji = null);
public sealed record ChatPollAnswerRow(long Id, string Text, string? Emoji, int Votes, bool Selected,
    IReadOnlyList<ChatUser> Users);
public sealed record ChatPollRow(DateTime StartsAtUtc, DateTime EndsAtUtc, bool AllowMultipleAnswers,
    bool IsOpen, bool HasStarted, int Voters, IReadOnlyList<ChatPollAnswerRow> Answers);

public static class ChatPollMapping
{
    public static void Configure(ModelBuilder model)
    {
        var poll = model.Entity<ChatPoll>();
        poll.ToTable("chat_polls", t => t.HasCheckConstraint("CK_chat_polls_duration",
            "ends_at_utc - starts_at_utc IN (interval '1 hour', interval '4 hours', interval '8 hours', interval '24 hours', interval '3 days', interval '7 days', interval '14 days')"));
        poll.HasKey(x => x.MessageId);
        poll.Property(x => x.MessageId).HasColumnName("message_id");
        poll.Property(x => x.StartsAtUtc).HasColumnName("starts_at_utc");
        poll.Property(x => x.EndsAtUtc).HasColumnName("ends_at_utc");
        poll.Property(x => x.AllowMultipleAnswers).HasColumnName("allow_multiple_answers");
        poll.HasOne(x => x.Message).WithOne().HasForeignKey<ChatPoll>(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        var answer = model.Entity<ChatPollAnswer>();
        answer.ToTable("chat_poll_answers", t => {
            t.HasCheckConstraint("CK_chat_poll_answers_text", "btrim(text) <> ''");
            t.HasCheckConstraint("CK_chat_poll_answers_position", "position >= 0");
        });
        answer.HasKey(x => x.Id);
        answer.HasAlternateKey(x => new { x.PollMessageId, x.Id });
        answer.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        answer.Property(x => x.PollMessageId).HasColumnName("poll_message_id");
        answer.Property(x => x.Position).HasColumnName("position");
        answer.Property(x => x.Text).HasColumnName("text").HasMaxLength(200);
        answer.Property(x => x.Emoji).HasColumnName("emoji").HasMaxLength(64);
        answer.HasIndex(x => new { x.PollMessageId, x.Position }).IsUnique();
        answer.HasOne(x => x.Poll).WithMany(x => x.Answers).HasForeignKey(x => x.PollMessageId).OnDelete(DeleteBehavior.Cascade);
        var vote = model.Entity<ChatPollVote>();
        vote.ToTable("chat_poll_votes");
        vote.HasKey(x => new { x.PollMessageId, x.UserId, x.AnswerId });
        vote.Property(x => x.PollMessageId).HasColumnName("poll_message_id");
        vote.Property(x => x.AnswerId).HasColumnName("answer_id");
        vote.Property(x => x.UserId).HasColumnName("user_id");
        vote.HasOne(x => x.Answer).WithMany(x => x.Votes)
            .HasForeignKey(x => new { x.PollMessageId, x.AnswerId })
            .HasPrincipalKey(x => new { x.PollMessageId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        vote.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
