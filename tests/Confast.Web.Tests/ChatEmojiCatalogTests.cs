using Confast.Web.Features.Chat;

namespace Confast.Web.Tests;

public sealed class ChatEmojiCatalogTests
{
    [Fact]
    public void UnicodeCatalog_ProvidesSearchableReactionsAcrossCategories()
    {
        Assert.True(ChatEmojiCatalog.All.Count > 3900);
        Assert.Contains(ChatEmojiCatalog.All, x => x.Emoji == "👍" && x.Name.Contains("thumbs up"));
        Assert.Contains(ChatEmojiCatalog.All, x => x.Emoji == "❤️" && x.Name.Contains("heart"));
        Assert.Contains(ChatEmojiCatalog.All, x => x.Emoji == "🎉" && x.Group == "Activities");
        Assert.Contains(ChatEmojiCatalog.All, x => x.Emoji == "🫫" && x.Version == 18);
        Assert.Contains(ChatEmojiCatalog.All, x => x.Emoji == "😀" && x.Version <= 1);
        Assert.All(ChatEmojiCatalog.All, x => Assert.InRange(x.Emoji.Length, 1, 32));
    }

    [Fact]
    public void SkinToneVariants_AppearUnderOneDefaultEmoji()
    {
        var thumbsUp = ChatEmojiCatalog.FindFamily("👍");
        Assert.NotNull(thumbsUp);
        Assert.Equal("👍", thumbsUp.Default.Emoji);
        Assert.Contains(thumbsUp.Tones, x => x.Emoji == "👍🏽");
        Assert.DoesNotContain(ChatEmojiCatalog.Families, x => x.Default.Emoji == "👍🏽");
        Assert.True(ChatEmojiCatalog.IsToneOf("👍", "👍🏽"));
        Assert.Equal("👍", ChatEmojiCatalog.FindFamilyForEmoji("👍🏽")?.Default.Emoji);
        Assert.False(ChatEmojiCatalog.IsToneOf("👍", "👎🏽"));
        Assert.Contains("🙂‍↔️", ChatEmojiCatalog.CompositeCandidates);
        Assert.All(ChatEmojiCatalog.Families.Where(x => x.Tones.Count > 1), family =>
            Assert.DoesNotContain(family.Default.Emoji.EnumerateRunes(),
                rune => rune.Value is >= 0x1F3FB and <= 0x1F3FF));
    }
}
