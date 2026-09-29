using AngleSharp.Html.Parser;
using Confast.Web.Features.Chat;

namespace Confast.Web.Tests;

public sealed class ChatMarkdownTests
{
    [Fact]
    public void RendersCommonAndExtendedMarkdown()
    {
        var html = ChatMarkdown.ToSafeHtml("# Heading\n\n**bold** and ~~gone~~\n\n- one\n- two\n\n| A | B |\n| - | - |\n| 1 | 2 |\n\n```cs\nvar x = 1;\n```");

        Assert.Contains("<h1", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<del>gone</del>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<code", html);
        Assert.Contains("<br", ChatMarkdown.ToSafeHtml("first\nsecond"));
    }

    [Fact]
    public void DoesNotAllowScriptsOrUnsafeLinks()
    {
        var html = ChatMarkdown.ToSafeHtml("<script>alert(1)</script> [bad](javascript:alert%281%29) ![bad](javascript:alert%281%29) [good](https://example.com)");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"https://example.com\"", html);
    }

    [Fact]
    public void WwwLinksOpenAsExternalHttpsWhileRelativeLinksStayRelative()
    {
        var html = ChatMarkdown.ToSafeHtml("[Google](www.google.com/search?q=chat) [Internal](/parts/42) [Local](notes/today)");

        Assert.Contains("href=\"https://www.google.com/search?q=chat\"", html);
        Assert.Contains("href=\"/parts/42\"", html);
        Assert.Contains("href=\"notes/today\"", html);
    }

    [Fact]
    public void ExternalWebLinksOpenInNewTabButApplicationAndMailLinksDoNot()
    {
        var html = ChatMarkdown.ToSafeHtml("[Secure](https://example.com) [Plain](http://example.com) [Www](www.google.com) [App](/parts/42) [Relative](notes/today) [Mail](mailto:someone@example.com)");
        var links = new HtmlParser().ParseDocument(html).QuerySelectorAll("a").ToArray();

        foreach (var link in links.Take(3))
        {
            Assert.Equal("_blank", link.GetAttribute("target"));
            Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        }
        foreach (var link in links.Skip(3))
        {
            Assert.Null(link.GetAttribute("target"));
            Assert.Null(link.GetAttribute("rel"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Just a plain message")]
    [InlineData("First line\nSecond line")]
    [InlineData("**unfinished")]
    [InlineData("`unfinished")]
    [InlineData("[unsafe](javascript:alert%281%29)")]
    public void PreviewStaysHiddenWithoutRenderedMarkdownFormatting(string source)
    {
        Assert.Null(ChatMarkdown.ToPreviewHtml(source));
    }

    [Theory]
    [InlineData("**bold**")]
    [InlineData("*italic*")]
    [InlineData("~~struck~~")]
    [InlineData("# Heading")]
    [InlineData("- list item")]
    [InlineData("> quoted text")]
    [InlineData("`code`")]
    [InlineData("```\ncode block\n```")]
    [InlineData("[Google](www.google.com)")]
    public void PreviewAppearsWhenMarkdownProducesFormatting(string source)
    {
        Assert.NotNull(ChatMarkdown.ToPreviewHtml(source));
    }
}
