using System.Text.RegularExpressions;

namespace Confast.Web.Features.Customers;

/// <summary>Shared, deliberately small brace-token language for certification templates.</summary>
public static partial class CertificationTemplateTokens
{
    public static string Render(string template, IReadOnlyDictionary<string, string> replacements, string templateDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var unknown = TokenPattern().Matches(template)
            .Select(match => match.Value)
            .Where(token => !replacements.ContainsKey(token[1..^1]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new CertificationTemplateException(
                $"Unknown {templateDescription} token{(unknown.Length == 1 ? string.Empty : "s")}: {string.Join(", ", unknown)}.");
        }

        if (TokenPattern().Replace(template, string.Empty).IndexOfAny(['{', '}']) >= 0)
        {
            throw new CertificationTemplateException($"The {templateDescription} contains an invalid token or unmatched brace.");
        }

        return TokenPattern().Replace(template, match => replacements[match.Value[1..^1]]);
    }

    [GeneratedRegex("\\{[^{}]+\\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

public sealed class CertificationTemplateException(string message) : ArgumentException(message);
