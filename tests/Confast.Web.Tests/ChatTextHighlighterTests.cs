using Confast.Web.Features.Chat;

namespace Confast.Web.Tests;

public sealed class ChatTextHighlighterTests
{
    [Fact]
    public void SourceIsEscapedBeforeColorMarkupIsAdded()
    {
        var html = ChatTextHighlighter.ToSafeHtml("#include <stdio.h>\n<script>alert(1)</script>", ChatTextLanguage.C);

        Assert.Contains("chat-code-directive", html);
        Assert.Contains("&lt;stdio.h&gt;", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void ChangingLanguageChangesFormattingWithoutChangingText()
    {
        const string source = "{\"answer\": 42, \"ready\": true}";
        var plain = ChatTextHighlighter.ToSafeHtml(source, ChatTextLanguage.PlainText);
        var json = ChatTextHighlighter.ToSafeHtml(source, ChatTextLanguage.Json);

        Assert.DoesNotContain("chat-code-", plain);
        Assert.Contains("chat-code-property", json);
        Assert.Contains("chat-code-number", json);
        Assert.Equal(ChatTextLanguage.C, ChatTextHighlighter.GuessLanguage("sample.c"));
    }
}
