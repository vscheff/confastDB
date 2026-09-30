using System.Text;
using Confast.Web.Features.Chat;

namespace Confast.Web.Tests;

public sealed class ChatAttachmentTypesTests
{
    [Fact]
    public void MediaRequiresExpectedSignature()
    {
        Assert.Equal(ChatAttachmentKind.Image,
            ChatAttachmentTypes.Classify("photo.PNG", [137, 80, 78, 71, 13, 10, 26, 10, 1]).Kind);
        Assert.Equal(ChatAttachmentKind.Download,
            ChatAttachmentTypes.Classify("photo.png", Encoding.UTF8.GetBytes("<script>x</script>")).Kind);
        Assert.Equal(ChatAttachmentKind.Video,
            ChatAttachmentTypes.Classify("clip.mp4", [0, 0, 0, 16, 102, 116, 121, 112, 1, 2, 3, 4]).Kind);
    }

    [Fact]
    public void TextRequiresUtf8AndTheTextLimit()
    {
        Assert.Equal(ChatAttachmentKind.Text,
            ChatAttachmentTypes.Classify("note.md", Encoding.UTF8.GetBytes("Hello")).Kind);
        Assert.Equal(ChatAttachmentKind.Text,
            ChatAttachmentTypes.Classify("source.c", Encoding.UTF8.GetBytes("#include <stdio.h>\nint main(void) { return 0; }")).Kind);
        Assert.Equal(ChatAttachmentKind.Download,
            ChatAttachmentTypes.Classify("note.txt", [0xff, 0xfe]).Kind);
        Assert.Equal(ChatAttachmentKind.Download,
            ChatAttachmentTypes.Classify("note.txt", new byte[ChatAttachmentTypes.MaximumTextBytes + 1]).Kind);
    }
}
