using Confast.Web.Features.Chat;

namespace Confast.Web.Tests;

public sealed class ChatMentionParserTests
{
    [Fact]
    public void FindsMemberNamesAndChannelTagsAtWordBoundaries()
    {
        ChatUser[] members = [new("one", "Ada Lovelace", "ada"), new("two", "Grace", "grace")];
        var mentions = ChatMentionParser.Find("Hi @Ada Lovelace, @here and @everyone. mail@grace.test @Graceful", members);

        Assert.Equal(["one"], mentions.Users);
        Assert.True(mentions.Here);
        Assert.True(mentions.Everyone);
    }

    [Fact]
    public void DuplicateDisplayNamesUseUsernames()
    {
        ChatUser[] members = [new("one", "Alex", "alex-one"), new("two", "Alex", "alex-two")];

        Assert.Equal("alex-two", ChatMentionParser.TagForMember(members[1], members));
        Assert.Empty(ChatMentionParser.Find("@Alex", members).Users);
        Assert.Equal(["two"], ChatMentionParser.Find("@alex-two", members).Users);
    }

    [Fact]
    public void NormalizesOnlyRecognizedChannelTags()
    {
        ChatUser[] members =
        [
            new("one", "Von Scheffler", "von"),
            new("two", "Alex", "alex-one"),
            new("three", "Alex", "alex-two")
        ];

        var body = ChatMentionParser.NormalizeTags(
            "Hi @vOn sChEfFlEr, @ALEx-TWO, @HeRe and @EvErYoNe. mail@von.test @unknown",
            members);

        Assert.Equal("Hi @Von Scheffler, @alex-two, @here and @everyone. mail@von.test @unknown", body);
        Assert.Equal("@von sc", ChatMentionParser.NormalizeTags("@von sc", members));
    }

    [Fact]
    public void OnlySavedUserMentionsBecomeClickableMessageParts()
    {
        ChatUser[] members = [new("one", "Ada Lovelace", "ada"), new("two", "Grace", "grace")];
        const string body = "Hi @Ada Lovelace, @Grace and @everyone.";
        var savedTokens = ChatMentionParser.FindTokens(body, members)
            .Where(x => x.UserId != "two").ToArray();
        var parts = ChatMentionParser.GetParts(body, savedTokens);

        Assert.Equal(["Hi ", "@Ada Lovelace", ", @Grace and ", "@everyone", "."],
            parts.Select(x => x.Text));
        Assert.Equal("one", parts[1].UserId);
        Assert.True(parts[1].IsTag);
        Assert.False(parts[2].IsTag);
        Assert.True(parts[3].IsTag);
        Assert.Null(parts[3].UserId);
        Assert.Equal([new ChatMessagePart(body, false)], ChatMentionParser.GetParts(body, []));
    }
}
