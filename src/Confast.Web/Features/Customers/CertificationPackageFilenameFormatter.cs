using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Confast.Web.Features.Customers;

public sealed partial class CertificationPackageFilenameFormatter
{
    public const string SystemDefaultTemplate = "{PartNumber}_{LotNumber}";

    public const string MultiLotSystemDefaultTemplate = "{CustomerName}";

    public static readonly IReadOnlyList<string> SupportedTokens =
    [
        "{CustomerName}",
        "{PartNumber}",
        "{LotNumber}",
        "{PONumber}",
        "{InspectionDate}",
        "{ShipDate}"
    ];

    public static readonly IReadOnlyList<string> MultiLotSupportedTokens =
    [
        "{CustomerName}",
        "{ShipDate}"
    ];

    private static readonly HashSet<char> InvalidFilenameCharacters =
        new("<>:\"/\\|?*".Concat(Path.GetInvalidFileNameChars()));

    public string Format(
        string? customerTemplate,
        CertificationPackageFilenameValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CustomerName"] = values.CustomerName,
            ["PartNumber"] = values.PartNumber,
            ["LotNumber"] = values.LotNumber ?? string.Empty,
            ["PONumber"] = values.PONumber ?? string.Empty,
            ["InspectionDate"] = values.InspectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["ShipDate"] = values.ShipDate?.ToString("MMddyy", CultureInfo.InvariantCulture) ?? string.Empty
        };

        return FormatTemplate(customerTemplate, SystemDefaultTemplate, replacements);
    }

    public string FormatMultiLot(
        string? customerTemplate,
        CertificationMultiLotPackageFilenameValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CustomerName"] = values.CustomerName,
            ["ShipDate"] = values.ShipDate?.ToString("MMddyy", CultureInfo.InvariantCulture) ?? string.Empty
        };

        return FormatTemplate(customerTemplate, MultiLotSystemDefaultTemplate, replacements);
    }

    private static string FormatTemplate(
        string? customerTemplate,
        string systemDefaultTemplate,
        IReadOnlyDictionary<string, string> replacements)
    {
        var template = string.IsNullOrWhiteSpace(customerTemplate)
            ? systemDefaultTemplate
            : customerTemplate.Trim();

        var unknownTokens = TokenPattern().Matches(template)
            .Select(match => match.Value)
            .Where(token => !replacements.ContainsKey(token[1..^1]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknownTokens.Length > 0)
        {
            throw new CertificationFilenameTemplateException(
                $"Unknown filename token{(unknownTokens.Length == 1 ? string.Empty : "s")}: {string.Join(", ", unknownTokens)}.");
        }

        var unresolvedBrace = TokenPattern().Replace(template, string.Empty);
        if (unresolvedBrace.Contains('{') || unresolvedBrace.Contains('}'))
        {
            throw new CertificationFilenameTemplateException("The filename template contains an invalid token or unmatched brace.");
        }

        var formatted = TokenPattern().Replace(
            template,
            match => SanitizeFilenameText(replacements[match.Value[1..^1]]));
        formatted = SanitizeFilenameText(formatted).Trim().TrimEnd('.', ' ');

        if (formatted.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            formatted = formatted[..^4].TrimEnd('.', ' ');
        }

        if (string.IsNullOrWhiteSpace(formatted))
        {
            throw new CertificationFilenameTemplateException("The filename template produces an empty filename.");
        }

        return $"{formatted}.pdf";
    }

    private static string SanitizeFilenameText(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            result.Append(character < 32 || InvalidFilenameCharacters.Contains(character)
                ? '_'
                : character);
        }

        return result.ToString();
    }

    [GeneratedRegex("\\{[^{}]+\\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

public sealed record CertificationPackageFilenameValues(
    string CustomerName,
    string PartNumber,
    string? LotNumber,
    string? PONumber,
    DateOnly InspectionDate,
    DateOnly? ShipDate = null);

public sealed record CertificationMultiLotPackageFilenameValues(
    string CustomerName,
    DateOnly? ShipDate = null);

public sealed class CertificationFilenameTemplateException(string message)
    : ArgumentException(message);
