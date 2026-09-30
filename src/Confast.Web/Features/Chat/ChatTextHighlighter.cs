using System.Net;
using System.Text;

namespace Confast.Web.Features.Chat;

public enum ChatTextLanguage
{
    PlainText, C, Cpp, CSharp, JavaScript, TypeScript, Python, Json, Html, Css, Sql, Markdown, Shell
}

public static class ChatTextHighlighter
{
    public static readonly (ChatTextLanguage Language, string Label)[] Languages =
    [
        (ChatTextLanguage.PlainText, "Plain text"), (ChatTextLanguage.C, "C"),
        (ChatTextLanguage.Cpp, "C++"), (ChatTextLanguage.CSharp, "C#"),
        (ChatTextLanguage.JavaScript, "JavaScript"), (ChatTextLanguage.TypeScript, "TypeScript"),
        (ChatTextLanguage.Python, "Python"), (ChatTextLanguage.Json, "JSON"),
        (ChatTextLanguage.Html, "HTML / XML"), (ChatTextLanguage.Css, "CSS"),
        (ChatTextLanguage.Sql, "SQL"), (ChatTextLanguage.Markdown, "Markdown"),
        (ChatTextLanguage.Shell, "Shell")
    ];

    private static readonly HashSet<string> CommonKeywords = Words(
        "break case catch class const continue default do else enum export extends false finally for function if import in interface let new null private protected public return static struct switch this throw true try typeof var void while yield");
    private static readonly HashSet<string> CKeywords = Words(
        "auto char double extern float goto inline int long register restrict short signed sizeof static typedef union unsigned volatile _Bool _Atomic");
    private static readonly HashSet<string> PythonKeywords = Words(
        "and as async await def del elif except from global is lambda nonlocal not or pass raise self with None True False");
    private static readonly HashSet<string> SqlKeywords = Words(
        "all alter and as asc between by case cast create cross delete desc distinct drop else end exists from full group having in inner insert into is join left like limit not null offset on or order outer primary references right select set table then union unique update values when where");

    public static ChatTextLanguage GuessLanguage(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".c" or ".h" => ChatTextLanguage.C,
        ".cc" or ".cpp" or ".hpp" or ".hh" => ChatTextLanguage.Cpp,
        ".cs" => ChatTextLanguage.CSharp,
        ".js" or ".jsx" => ChatTextLanguage.JavaScript,
        ".ts" or ".tsx" => ChatTextLanguage.TypeScript,
        ".py" => ChatTextLanguage.Python,
        ".json" => ChatTextLanguage.Json,
        ".html" or ".htm" or ".xml" => ChatTextLanguage.Html,
        ".css" => ChatTextLanguage.Css,
        ".sql" => ChatTextLanguage.Sql,
        ".md" => ChatTextLanguage.Markdown,
        ".sh" or ".ps1" => ChatTextLanguage.Shell,
        _ => ChatTextLanguage.PlainText
    };

    public static string Label(ChatTextLanguage language) =>
        Languages.First(x => x.Language == language).Label;

    public static string ToSafeHtml(string? text, ChatTextLanguage language)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (language == ChatTextLanguage.PlainText) return WebUtility.HtmlEncode(text);

        var html = new StringBuilder(text.Length + text.Length / 4);
        for (var i = 0; i < text.Length;)
        {
            var start = i;
            var current = text[i];
            if (language == ChatTextLanguage.Markdown && current == '#' && IsLineStart(text, i))
            {
                while (i < text.Length && text[i] is not ('\r' or '\n')) i++;
                AppendToken(html, text, start, i, "heading");
            }
            else if (language == ChatTextLanguage.Html && StartsWith(text, i, "<!--"))
            {
                i = Through(text, i + 4, "-->");
                AppendToken(html, text, start, i, "comment");
            }
            else if (language == ChatTextLanguage.Html && current == '<' && i + 1 < text.Length)
            {
                i = ScanTag(text, i + 1);
                AppendToken(html, text, start, i, "tag");
            }
            else if (IsPreprocessor(language) && current == '#' && IsLineStart(text, i))
            {
                i++;
                while (i < text.Length && char.IsLetter(text[i])) i++;
                AppendToken(html, text, start, i, "directive");
            }
            else if (IsCommentStart(text, i, language, out var commentEnd))
            {
                i = commentEnd;
                AppendToken(html, text, start, i, "comment");
            }
            else if (IsQuote(current, language))
            {
                i = ScanQuoted(text, i, current);
                var next = i;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                AppendToken(html, text, start, i,
                    language == ChatTextLanguage.Json && next < text.Length && text[next] == ':' ? "property" : "string");
            }
            else if (current == '<' && IsPreprocessor(language) && IsIncludePath(text, i))
            {
                i = Through(text, i + 1, ">");
                AppendToken(html, text, start, i, "string");
            }
            else if (char.IsDigit(current) && (i == 0 || !IsIdentifierPart(text[i - 1])))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '_' or 'x' or 'X')) i++;
                AppendToken(html, text, start, i, "number");
            }
            else if (IsIdentifierStart(current))
            {
                i++;
                while (i < text.Length && IsIdentifierPart(text[i])) i++;
                var word = text[start..i];
                AppendToken(html, text, start, i, IsKeyword(word, language) ? "keyword" : null);
            }
            else
            {
                i++;
                AppendEscaped(html, text.AsSpan(start, i - start));
            }
        }
        return html.ToString();
    }

    private static bool IsKeyword(string word, ChatTextLanguage language) => language switch
    {
        ChatTextLanguage.Json => word is "true" or "false" or "null",
        ChatTextLanguage.Sql => SqlKeywords.Contains(word.ToLowerInvariant()),
        ChatTextLanguage.Python => PythonKeywords.Contains(word) || CommonKeywords.Contains(word),
        ChatTextLanguage.C or ChatTextLanguage.Cpp => CKeywords.Contains(word) || CommonKeywords.Contains(word),
        _ => CommonKeywords.Contains(word)
    };

    private static bool IsPreprocessor(ChatTextLanguage language) =>
        language is ChatTextLanguage.C or ChatTextLanguage.Cpp or ChatTextLanguage.CSharp;

    private static bool IsQuote(char current, ChatTextLanguage language) => current == '"' ||
        (current == '\'' && language != ChatTextLanguage.Json) ||
        (current == '`' && language is ChatTextLanguage.JavaScript or ChatTextLanguage.TypeScript);

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value is '_' or '$';
    private static bool IsIdentifierPart(char value) => IsIdentifierStart(value) || char.IsDigit(value);

    private static bool IsLineStart(string text, int index)
    {
        while (index > 0 && text[index - 1] is not ('\n' or '\r'))
        {
            if (!char.IsWhiteSpace(text[--index])) return false;
        }
        return true;
    }

    private static bool IsIncludePath(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        return text.AsSpan(lineStart, index - lineStart).TrimStart().StartsWith("#include", StringComparison.Ordinal);
    }

    private static bool IsCommentStart(string text, int index, ChatTextLanguage language, out int end)
    {
        string? marker = null;
        if (StartsWith(text, index, "//") && language is not (ChatTextLanguage.Python or ChatTextLanguage.Sql or ChatTextLanguage.Markdown or ChatTextLanguage.Shell))
            marker = "//";
        else if (StartsWith(text, index, "/*") && language is not (ChatTextLanguage.Python or ChatTextLanguage.Sql or ChatTextLanguage.Markdown or ChatTextLanguage.Shell))
        {
            end = Through(text, index + 2, "*/");
            return true;
        }
        else if (StartsWith(text, index, "--") && language == ChatTextLanguage.Sql) marker = "--";
        else if (text[index] == '#' && language is ChatTextLanguage.Python or ChatTextLanguage.Shell) marker = "#";
        if (marker is null) { end = index; return false; }
        end = index + marker.Length;
        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        return true;
    }

    private static int ScanQuoted(string text, int index, char quote)
    {
        index++;
        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length) { index += 2; continue; }
            if (text[index++] == quote) break;
        }
        return index;
    }

    private static int ScanTag(string text, int index)
    {
        char quote = '\0';
        while (index < text.Length)
        {
            var current = text[index++];
            if (quote != '\0') { if (current == quote) quote = '\0'; }
            else if (current is '"' or '\'') quote = current;
            else if (current == '>') break;
        }
        return index;
    }

    private static int Through(string text, int from, string marker)
    {
        var position = text.IndexOf(marker, from, StringComparison.Ordinal);
        return position < 0 ? text.Length : position + marker.Length;
    }

    private static bool StartsWith(string text, int index, string value) =>
        text.AsSpan(index).StartsWith(value, StringComparison.Ordinal);

    private static void AppendToken(StringBuilder html, string text, int start, int end, string? token)
    {
        if (token is not null) html.Append("<span class=\"chat-code-").Append(token).Append("\">");
        AppendEscaped(html, text.AsSpan(start, end - start));
        if (token is not null) html.Append("</span>");
    }

    private static void AppendEscaped(StringBuilder html, ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            switch (character)
            {
                case '&': html.Append("&amp;"); break;
                case '<': html.Append("&lt;"); break;
                case '>': html.Append("&gt;"); break;
                case '"': html.Append("&quot;"); break;
                case '\'': html.Append("&#39;"); break;
                default: html.Append(character); break;
            }
        }
    }

    private static HashSet<string> Words(string words) =>
        words.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
}
