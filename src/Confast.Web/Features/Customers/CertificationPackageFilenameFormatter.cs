using System.Globalization;
using System.Text;

namespace Confast.Web.Features.Customers;

public sealed partial class CertificationPackageFilenameFormatter
{
    public const string SystemDefaultTemplate = "{PartNumber}_Lot#_{LotNumber}";

    public const string SinglePartMultiLotSystemDefaultTemplate = "{PartNumber}";

    public const string MultiPartSystemDefaultTemplate = "{CustomerName}";

    public static readonly IReadOnlyList<string> SupportedTokens =
    [
        "{CustomerName}",
        "{PlantName}",
        "{PlantCode}",
        "{PartNumber}",
        "{LotNumber}",
        "{PONumber}",
        "{InspectionDate}",
        "{ShipDate}"
    ];

    public static readonly IReadOnlyList<string> SinglePartMultiLotSupportedTokens =
    [
        "{CustomerName}",
        "{PlantName}",
        "{PlantCode}",
        "{PartNumber}",
        "{ShipDate}"
    ];

    public static readonly IReadOnlyList<string> MultiPartSupportedTokens =
    [
        "{CustomerName}",
        "{PlantName}",
        "{PlantCode}",
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
            ["PlantName"] = values.PlantName,
            ["PlantCode"] = values.PlantCode ?? string.Empty,
            ["PartNumber"] = values.PartNumber,
            ["LotNumber"] = values.LotNumber ?? string.Empty,
            ["PONumber"] = values.PONumber ?? string.Empty,
            ["InspectionDate"] = values.InspectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["ShipDate"] = values.ShipDate?.ToString("MMddyy", CultureInfo.InvariantCulture) ?? string.Empty
        };

        return FormatTemplate(customerTemplate, SystemDefaultTemplate, replacements);
    }

    public string FormatSinglePartMultiLot(
        string? customerTemplate,
        CertificationSinglePartMultiLotPackageFilenameValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CustomerName"] = values.CustomerName,
            ["PlantName"] = values.PlantName,
            ["PlantCode"] = values.PlantCode ?? string.Empty,
            ["PartNumber"] = values.PartNumber,
            ["ShipDate"] = values.ShipDate?.ToString("MMddyy", CultureInfo.InvariantCulture) ?? string.Empty
        };

        return FormatTemplate(customerTemplate, SinglePartMultiLotSystemDefaultTemplate, replacements);
    }

    public string FormatMultiPart(
        string? customerTemplate,
        CertificationMultiPartPackageFilenameValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CustomerName"] = values.CustomerName,
            ["PlantName"] = values.PlantName,
            ["PlantCode"] = values.PlantCode ?? string.Empty,
            ["ShipDate"] = values.ShipDate?.ToString("MMddyy", CultureInfo.InvariantCulture) ?? string.Empty
        };

        return FormatTemplate(customerTemplate, MultiPartSystemDefaultTemplate, replacements);
    }

    private static string FormatTemplate(
        string? customerTemplate,
        string systemDefaultTemplate,
        IReadOnlyDictionary<string, string> replacements)
    {
        var template = string.IsNullOrWhiteSpace(customerTemplate)
            ? systemDefaultTemplate
            : customerTemplate.Trim();

        string rendered;
        try
        {
            rendered = CertificationTemplateTokens.Render(
                template,
                replacements.ToDictionary(x => x.Key, x => SanitizeFilenameText(x.Value), StringComparer.Ordinal),
                "filename template");
        }
        catch (CertificationTemplateException exception)
        {
            throw new CertificationFilenameTemplateException(exception.Message);
        }

        var formatted = rendered;
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

}

public sealed record CertificationPackageFilenameValues(
    string CustomerName,
    string PartNumber,
    string? LotNumber,
    string? PONumber,
    DateOnly InspectionDate,
    DateOnly? ShipDate = null,
    string PlantName = "",
    string? PlantCode = null);

public sealed record CertificationSinglePartMultiLotPackageFilenameValues(
    string CustomerName,
    string PartNumber,
    DateOnly? ShipDate = null,
    string PlantName = "",
    string? PlantCode = null);

public sealed record CertificationMultiPartPackageFilenameValues(
    string CustomerName,
    DateOnly? ShipDate = null,
    string PlantName = "",
    string? PlantCode = null);

public sealed class CertificationFilenameTemplateException(string message)
    : ArgumentException(message);
