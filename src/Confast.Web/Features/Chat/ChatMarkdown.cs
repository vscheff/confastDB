using AngleSharp.Html.Parser;
using Ganss.Xss;
using Markdig;

namespace Confast.Web.Features.Chat;

public static class ChatMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    public static string? ToPreviewHtml(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var html = ToSafeHtml(body);
        var document = new HtmlParser().ParseDocument(html);
        return document.QuerySelector("strong, em, del, s, code, pre, blockquote, hr, h1, h2, h3, h4, h5, h6, ul, ol, a[href], img[src], table, sup, sub") is null
            ? null : html;
    }

    public static string ToSafeHtml(string body)
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "strong", "em", "del", "s", "code", "pre", "blockquote", "hr",
            "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "a", "img",
            "table", "thead", "tbody", "tr", "th", "td", "input", "sup", "sub"
        }) sanitizer.AllowedTags.Add(tag);

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "src", "title", "alt", "class", "type", "checked", "disabled", "start" })
            sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");

        var html = sanitizer.Sanitize(Markdown.ToHtml(body, Pipeline));
        var document = new HtmlParser().ParseDocument(html);
        foreach (var link in document.QuerySelectorAll("a[href]"))
        {
            var href = link.GetAttribute("href");
            if (href is null) continue;

            // Browsers resolve scheme-less Markdown destinations against this application's URL.
            if (href.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate($"https://{href}", UriKind.Absolute, out var normalized)
                && normalized.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                && normalized.UserInfo.Length == 0)
            {
                href = normalized.AbsoluteUri;
                link.SetAttribute("href", href);
            }

            if (Uri.TryCreate(href, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                link.SetAttribute("target", "_blank");
                link.SetAttribute("rel", "noopener noreferrer");
            }
        }
        return document.Body!.InnerHtml;
    }

}
