using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Confast.Web.Data;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Ganss.Xss;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace Confast.Web.Features.Inspections;

public enum CertificationEmailTemplateType { SingleLot, SinglePartMultiLot, MultiPart }

public sealed class CertificationEmailTemplate
{
    public CertificationEmailTemplateType TemplateType { get; set; }
    public string SubjectTemplate { get; set; } = string.Empty;
    public string HtmlBodyTemplate { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }
    public ApplicationUser? UpdatedByUser { get; set; }
}

public sealed class CertificationEmailSettings
{
    public int Id { get; set; }
    public string? ImplicitCcAddress { get; set; }
}

public sealed record CertificationEmailTemplateEditModel(
    CertificationEmailTemplateType TemplateType,
    string SubjectTemplate,
    string HtmlBodyTemplate,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedByUserId);

public sealed record CertificationEmailSettingsEditModel(string? ImplicitCcAddress);

public sealed record CertificationEmailDraft(
    CertificationEmailTemplateType TemplateType,
    string Subject,
    string HtmlBody,
    IReadOnlyList<string> ToRecipients,
    IReadOnlyList<string> CcRecipients,
    string AttachmentName);

public interface ICertificationEmailTemplateResolver
{
    Task<CertificationEmailTemplateEditModel> ResolveEmailTemplateAsync(CertificationEmailTemplateType templateType, CancellationToken cancellationToken = default);
}

public sealed class CertificationEmailTemplateResolver(IDbContextFactory<AppDbContext> contextFactory) : ICertificationEmailTemplateResolver
{
    public async Task<CertificationEmailTemplateEditModel> ResolveEmailTemplateAsync(CertificationEmailTemplateType templateType, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var template = await db.CertificationEmailTemplates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TemplateType == templateType, cancellationToken)
            ?? throw new CertificationEmailTemplateException($"No {templateType} certification email template is configured.");
        return ToModel(template);
    }

    internal static CertificationEmailTemplateEditModel ToModel(CertificationEmailTemplate template) => new(
        template.TemplateType, template.SubjectTemplate, template.HtmlBodyTemplate, template.UpdatedAtUtc, template.UpdatedByUserId);
}

public interface ICertificationEmailTemplateService
{
    Task<IReadOnlyList<CertificationEmailTemplateEditModel>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CertificationEmailTemplateEditModel model, CancellationToken cancellationToken = default);
    Task<CertificationEmailSettingsEditModel> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(CertificationEmailSettingsEditModel model, CancellationToken cancellationToken = default);
}

public sealed class CertificationEmailTemplateService(
    IDbContextFactory<AppDbContext> contextFactory,
    ICurrentUser currentUser,
    CertificationEmailTemplateRenderer renderer,
    CertificationEmailHtmlSanitizer sanitizer) : ICertificationEmailTemplateService
{
    public async Task<IReadOnlyList<CertificationEmailTemplateEditModel>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.CertificationEmailTemplates.AsNoTracking().OrderBy(x => x.TemplateType)
            .Select(x => new CertificationEmailTemplateEditModel(x.TemplateType, x.SubjectTemplate, x.HtmlBodyTemplate, x.UpdatedAtUtc, x.UpdatedByUserId))
            .ToListAsync(cancellationToken);
    }

    public async Task SaveAsync(CertificationEmailTemplateEditModel model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model.SubjectTemplate)) throw new CertificationEmailTemplateException("Email subject is required.");
        if (string.IsNullOrWhiteSpace(model.HtmlBodyTemplate)) throw new CertificationEmailTemplateException("Email body is required.");
        renderer.ValidateTemplate(model.TemplateType, model.SubjectTemplate, model.HtmlBodyTemplate);
        var body = sanitizer.Sanitize(model.HtmlBodyTemplate);
        if (string.IsNullOrWhiteSpace(body)) throw new CertificationEmailTemplateException("The email body contains no supported content.");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var template = await db.CertificationEmailTemplates.SingleOrDefaultAsync(x => x.TemplateType == model.TemplateType, cancellationToken)
            ?? throw new CertificationEmailTemplateException($"No {model.TemplateType} certification email template is configured.");
        template.SubjectTemplate = model.SubjectTemplate.Trim();
        template.HtmlBodyTemplate = body;
        template.UpdatedAtUtc = DateTimeOffset.UtcNow;
        template.UpdatedByUserId = await currentUser.GetUserIdAsync();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CertificationEmailSettingsEditModel> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.CertificationEmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        return new CertificationEmailSettingsEditModel(settings?.ImplicitCcAddress);
    }

    public async Task SaveSettingsAsync(CertificationEmailSettingsEditModel model, CancellationToken cancellationToken = default)
    {
        var address = NormalizeImplicitCcAddress(model.ImplicitCcAddress);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.CertificationEmailSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is null)
        {
            settings = new CertificationEmailSettings { Id = 1 };
            db.CertificationEmailSettings.Add(settings);
        }
        settings.ImplicitCcAddress = address;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static string? NormalizeImplicitCcAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var address = value.Trim();
        if (!MailboxAddress.TryParse(address, out var mailbox)
            || !string.Equals(address, mailbox.Address, StringComparison.OrdinalIgnoreCase))
        {
            throw new CertificationEmailTemplateException("Enter a valid email address for the implicit CC, or leave it blank.");
        }
        return mailbox.Address;
    }
}

public sealed partial class CertificationEmailHtmlSanitizer
{
    private const int MaximumInlineImageCharacters = 4_000_000;
    private readonly HtmlSanitizer sanitizer = CreateSanitizer();
    public string Sanitize(string html)
    {
        var sanitized = sanitizer.Sanitize(html ?? string.Empty);
        sanitized = ImageTagPattern().Replace(sanitized, match => IsSafeImageTag(match.Value) ? match.Value : string.Empty);
        return AnchorTagPattern().Replace(sanitized, match => IsSafeAnchorTag(match.Value) ? match.Value : StripHref(match.Value));
    }

    public string SanitizeForEmail(string html)
    {
        var sanitized = Sanitize(html);
        sanitized = RgbaColorPattern().Replace(sanitized, match =>
        {
            if (!double.TryParse(match.Groups["alpha"].Value, CultureInfo.InvariantCulture, out var alpha) || alpha != 1) return match.Value;
            if (!int.TryParse(match.Groups["red"].Value, CultureInfo.InvariantCulture, out var red)
                || !int.TryParse(match.Groups["green"].Value, CultureInfo.InvariantCulture, out var green)
                || !int.TryParse(match.Groups["blue"].Value, CultureInfo.InvariantCulture, out var blue)) return match.Value;
            return $"#{Math.Clamp(red, 0, 255):X2}{Math.Clamp(green, 0, 255):X2}{Math.Clamp(blue, 0, 255):X2}";
        });
        return ParagraphTagPattern().Replace(sanitized, match =>
        {
            var tag = match.Value;
            var style = StyleAttributePattern().Match(tag);
            if (!style.Success) return "<p style=\"margin:0; line-height:1.4;\">";
            var value = style.Groups["style"].Value.Trim();
            if (!value.EndsWith(';')) value += ";";
            return StyleAttributePattern().Replace(tag, $"style=\"margin:0; line-height:1.4; {value}\"");
        });
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var result = new HtmlSanitizer();
        result.AllowedTags.Clear(); result.AllowedTags.UnionWith(["p", "br", "strong", "b", "em", "i", "u", "span", "font", "ul", "ol", "li", "a", "img", "table", "thead", "tbody", "tr", "th", "td"]);
        result.AllowedAttributes.Clear(); result.AllowedAttributes.UnionWith(["href", "style", "color", "target", "rel", "src", "alt", "width", "height", "colspan", "rowspan"]);
        result.AllowedCssProperties.Clear(); result.AllowedCssProperties.UnionWith(["font-family", "font-size", "color", "text-align", "font-weight", "font-style", "text-decoration", "line-height", "margin"]);
        result.AllowedSchemes.Clear(); result.AllowedSchemes.UnionWith(["http", "https", "mailto", "data"]);
        return result;
    }

    private static bool IsSafeImageTag(string tag)
    {
        var source = SourcePattern().Match(tag).Groups["source"].Value;
        if (source.Length == 0 || source.Length > MaximumInlineImageCharacters) return false;
        if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        return SafeDataImagePattern().IsMatch(source);
    }

    private static bool IsSafeAnchorTag(string tag)
    {
        var href = HrefPattern().Match(tag).Groups["href"].Value;
        return href.Length == 0 || !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripHref(string tag) => HrefPattern().Replace(tag, string.Empty);

    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTagPattern();

    [GeneratedRegex("<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnchorTagPattern();

    [GeneratedRegex("\\bsrc\\s*=\\s*[\\\"'](?<source>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourcePattern();

    [GeneratedRegex("\\bhref\\s*=\\s*[\\\"'](?<href>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefPattern();

    [GeneratedRegex("<p\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphTagPattern();

    [GeneratedRegex("\\bstyle\\s*=\\s*[\\\"'](?<style>[^\\\"']*)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleAttributePattern();

    [GeneratedRegex("rgba\\(\\s*(?<red>\\d+)\\s*,\\s*(?<green>\\d+)\\s*,\\s*(?<blue>\\d+)\\s*,\\s*(?<alpha>[0-9.]+)\\s*\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RgbaColorPattern();

    [GeneratedRegex("^data:image/(?:png|jpe?g|gif|webp);base64,[a-z0-9+/=\\s]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SafeDataImagePattern();
}

public sealed class CertificationEmailTemplateRenderer
{
    public static IReadOnlyList<string> SubjectTokensFor(CertificationEmailTemplateType type) => type switch
    {
        CertificationEmailTemplateType.SingleLot => ["{CustomerName}", "{PlantName}", "{PlantCode}", "{ShipDate}", "{ShipDayOfWeek}", "{CertificationPackageFilename}", "{SelectedLotCount}", "{SelectedPartCount}", "{PartNumber}", "{LotNumber}"],
        CertificationEmailTemplateType.SinglePartMultiLot => ["{CustomerName}", "{PlantName}", "{PlantCode}", "{ShipDate}", "{ShipDayOfWeek}", "{CertificationPackageFilename}", "{SelectedLotCount}", "{SelectedPartCount}", "{PartNumber}", "{LotNumbers}"],
        _ => ["{CustomerName}", "{PlantName}", "{PlantCode}", "{ShipDate}", "{ShipDayOfWeek}", "{CertificationPackageFilename}", "{SelectedLotCount}", "{SelectedPartCount}", "{PartLotSummary}"]
    };

    public static IReadOnlyList<string> BodyTokensFor(CertificationEmailTemplateType type) => SubjectTokensFor(type)
        .Concat(type == CertificationEmailTemplateType.MultiPart ? ["{PartNumbers}"] : [])
        .Concat(["{UserDisplayName}", "{UserJobTitle}", "{UserEmail}"]).ToArray();

    public static IReadOnlyList<string> TokensFor(CertificationEmailTemplateType type) => BodyTokensFor(type);

    public CertificationEmailTemplateType GetTemplateType(CertificationPackage package) => package.Lots.Count switch
    {
        1 => CertificationEmailTemplateType.SingleLot,
        _ when package.Lots.Select(x => x.PartId).Distinct().Count() == 1 => CertificationEmailTemplateType.SinglePartMultiLot,
        _ => CertificationEmailTemplateType.MultiPart
    };

    public void ValidateTemplate(CertificationEmailTemplateType type, string subject, string htmlBody)
    {
        try
        {
            CertificationTemplateTokens.Render(subject, PlaceholderValues(SubjectTokensFor(type)), "email subject template");
            CertificationTemplateTokens.Render(htmlBody, PlaceholderValues(BodyTokensFor(type)), "email body template");
        }
        catch (CertificationTemplateException exception)
        {
            throw new CertificationEmailTemplateException(exception.Message);
        }
    }

    public CertificationEmailDraft Render(CertificationPackage package, CertificationEmailTemplateEditModel template, EmailSenderIdentity? sender = null)
    {
        var type = GetTemplateType(package);
        if (template.TemplateType != type) throw new CertificationEmailTemplateException("The selected email template does not match the certification package.");
        var text = Values(package, sender, html: false);
        var html = Values(package, sender, html: true);
        try
        {
            return new CertificationEmailDraft(type,
                CertificationTemplateTokens.Render(template.SubjectTemplate, text, "email subject template"),
                CertificationTemplateTokens.Render(template.HtmlBodyTemplate, html, "email body template"),
                package.ToRecipients, package.CcRecipients, package.FileName);
        }
        catch (CertificationTemplateException exception)
        {
            throw new CertificationEmailTemplateException(exception.Message);
        }
    }

    private static IReadOnlyDictionary<string, string> PlaceholderValues(IEnumerable<string> tokens) => tokens
        .ToDictionary(x => x[1..^1], _ => "Example", StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> Values(CertificationPackage package, EmailSenderIdentity? sender, bool html)
    {
        string Text(string value) => html ? WebUtility.HtmlEncode(value) : value;
        var lots = package.Lots;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CustomerName"] = Text(package.CustomerName), ["PlantName"] = Text(package.PlantName), ["PlantCode"] = Text(package.PlantCode ?? string.Empty),
            ["ShipDate"] = Text(package.ShipDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)),
            ["ShipDayOfWeek"] = Text(package.ShipDate.ToString("dddd", CultureInfo.InvariantCulture)),
            ["CertificationPackageFilename"] = Text(package.FileName),
            ["SelectedLotCount"] = lots.Count.ToString(), ["SelectedPartCount"] = lots.Select(x => x.PartId).Distinct().Count().ToString(),
            ["UserDisplayName"] = Text(sender?.DisplayName ?? string.Empty), ["UserJobTitle"] = Text(sender?.JobTitle ?? string.Empty),
            ["UserEmail"] = Text(sender?.EmailAddress ?? string.Empty)
        };
        if (lots.Count == 1) { values["PartNumber"] = Text(lots[0].PartNumber); values["LotNumber"] = Text(lots[0].LotNumber ?? lots[0].InspectionId.ToString()); }
        else if (lots.Select(x => x.PartId).Distinct().Count() == 1) { values["PartNumber"] = Text(lots[0].PartNumber); values["LotNumbers"] = Text(string.Join(", ", lots.Select(x => x.LotNumber ?? x.InspectionId.ToString()))); }
        else
        {
            values["PartNumbers"] = Text(string.Join(", ", lots.Select(x => x.PartNumber).Distinct(StringComparer.Ordinal)));
            values["PartLotSummary"] = html ? string.Join(string.Empty, lots.GroupBy(x => x.PartNumber).Select(group => $"<tr><td>{WebUtility.HtmlEncode(group.Key)}</td><td>{WebUtility.HtmlEncode(string.Join(", ", group.Select(x => x.LotNumber ?? x.InspectionId.ToString())))}</td></tr>").Prepend("<table><thead><tr><th>Part</th><th>Lots</th></tr></thead><tbody>").Append("</tbody></table>")) : string.Join("; ", lots.GroupBy(x => x.PartNumber).Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.LotNumber ?? x.InspectionId.ToString()))}"));
        }
        return values;
    }
}

public sealed class CertificationEmailTemplateException(string message) : InvalidOperationException(message);
