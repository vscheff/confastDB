using Confast.Web.Features.Customers;
using Confast.Web.Features.Inspections;

namespace Confast.Web.Tests;

public sealed class CertificationEmailTemplateRendererTests
{
    private readonly CertificationEmailTemplateRenderer renderer = new();

    [Theory]
    [InlineData(CertificationEmailTemplateType.SingleLot, CertificationEmailTemplateType.SingleLot)]
    [InlineData(CertificationEmailTemplateType.SinglePartMultiLot, CertificationEmailTemplateType.SinglePartMultiLot)]
    [InlineData(CertificationEmailTemplateType.MultiPart, CertificationEmailTemplateType.MultiPart)]
    public void SelectsTemplateTypeFromActualPackageShape(CertificationEmailTemplateType shape, CertificationEmailTemplateType expected)
    {
        Assert.Equal(expected, renderer.GetTemplateType(Package(shape)));
    }

    [Fact]
    public void RendersAggregateLotsAndHtmlEncodedMultiPartSummary()
    {
        var package = Package(CertificationEmailTemplateType.MultiPart) with
        {
            Lots = [
                new CertificationPackageLot(1, 10, "<unsafe>", "Part <123>", new DateOnly(2026, 8, 28), [], []),
                new CertificationPackageLot(2, 10, "B", "Part <123>", new DateOnly(2026, 8, 28), [], []),
                new CertificationPackageLot(3, 11, "C", "Other", new DateOnly(2026, 8, 28), [], [])]
        };
        var draft = renderer.Render(package, Template(CertificationEmailTemplateType.MultiPart, "{SelectedPartCount} parts", "{PartLotSummary}"));

        Assert.Equal("2 parts", draft.Subject);
        Assert.Contains("Part &lt;123&gt;", draft.HtmlBody);
        Assert.Contains("&lt;unsafe&gt;, B", draft.HtmlBody);
        Assert.Contains("<table>", draft.HtmlBody);
    }

    [Fact]
    public void RendersDistinctPartNumbersForAMultiPartBody()
    {
        var package = Package(CertificationEmailTemplateType.MultiPart) with
        {
            Lots = [
                new CertificationPackageLot(1, 10, "A", "Part 1", new DateOnly(2026, 8, 28), [], []),
                new CertificationPackageLot(2, 10, "B", "Part 1", new DateOnly(2026, 8, 28), [], []),
                new CertificationPackageLot(3, 11, "C", "Part 2", new DateOnly(2026, 8, 28), [], [])]
        };

        var draft = renderer.Render(package, Template(CertificationEmailTemplateType.MultiPart, "Certification package", "<p>{PartNumbers}</p>"));

        Assert.Contains("Part 1, Part 2", draft.HtmlBody);
        Assert.Contains("{PartNumbers}", CertificationEmailTemplateRenderer.BodyTokensFor(CertificationEmailTemplateType.MultiPart));
        Assert.DoesNotContain("{PartNumbers}", CertificationEmailTemplateRenderer.SubjectTokensFor(CertificationEmailTemplateType.MultiPart));
    }

    [Fact]
    public void RejectsTokensUnavailableToTheTemplateShape()
    {
        var exception = Assert.Throws<CertificationEmailTemplateException>(() =>
            renderer.ValidateTemplate(CertificationEmailTemplateType.MultiPart, "{LotNumber}", "<p>Body</p>"));

        Assert.Contains("{LotNumber}", exception.Message);
    }

    [Fact]
    public void RendersPlantAndSendingUserTokensInTheirAllowedContexts()
    {
        var package = Package(CertificationEmailTemplateType.SingleLot) with { PlantCode = "NP-1" };
        var template = Template(CertificationEmailTemplateType.SingleLot,
            "{PlantCode} certification",
            "<p>{PlantCode} / {UserDisplayName} / {UserJobTitle}</p>");

        var draft = renderer.Render(package, template, new EmailSenderIdentity("Quality Person", "quality@example.com", "Quality Manager"));

        Assert.Equal("NP-1 certification", draft.Subject);
        Assert.Contains("NP-1 / Quality Person / Quality Manager", draft.HtmlBody);
        Assert.Contains("{PlantCode}", CertificationEmailTemplateRenderer.SubjectTokensFor(CertificationEmailTemplateType.SingleLot));
        Assert.Contains("{PlantCode}", CertificationEmailTemplateRenderer.BodyTokensFor(CertificationEmailTemplateType.SingleLot));
        Assert.Contains("{UserDisplayName}", CertificationEmailTemplateRenderer.BodyTokensFor(CertificationEmailTemplateType.SingleLot));
        Assert.DoesNotContain("{UserDisplayName}", CertificationEmailTemplateRenderer.SubjectTokensFor(CertificationEmailTemplateType.SingleLot));
    }

    [Fact]
    public void RendersShipDateAndDayOfWeekUsingRequestedFormats()
    {
        var package = Package(CertificationEmailTemplateType.SingleLot) with { ShipDate = new DateOnly(2026, 8, 31) };
        var template = Template(CertificationEmailTemplateType.SingleLot, "Ships {ShipDayOfWeek} {ShipDate}", "<p>{ShipDayOfWeek}, {ShipDate}</p>");

        var draft = renderer.Render(package, template);

        Assert.Equal("Ships Monday 08/31/2026", draft.Subject);
        Assert.Contains("Monday, 08/31/2026", draft.HtmlBody);
    }

    [Fact]
    public void RendersUserEmailInsideAValidatedMailtoLink()
    {
        var package = Package(CertificationEmailTemplateType.SingleLot);
        var template = Template(CertificationEmailTemplateType.SingleLot, "Subject", "<p><a href=\"mailto:{UserEmail}\">Contact me</a></p>");
        var sender = new EmailSenderIdentity("Quality Person", "quality.person@example.com", "Quality Manager");
        var sanitizer = new CertificationEmailHtmlSanitizer();

        var savedHtml = sanitizer.Sanitize(template.HtmlBodyTemplate);
        var draft = renderer.Render(package, template with { HtmlBodyTemplate = savedHtml }, sender);
        var finalHtml = sanitizer.Sanitize(draft.HtmlBody);

        Assert.Contains("href=\"mailto:quality.person@example.com\"", finalHtml);
        Assert.Contains("Contact me", finalHtml);
        Assert.Contains("{UserEmail}", CertificationEmailTemplateRenderer.BodyTokensFor(CertificationEmailTemplateType.SingleLot));
    }

    [Fact]
    public void SanitizerRemovesExecutableMarkupAndJavascriptLinks()
    {
        var result = new CertificationEmailHtmlSanitizer().Sanitize("<p onclick='alert(1)'>Hi</p><script>alert(1)</script><a href='javascript:alert(1)'>bad</a><strong>safe</strong>");

        Assert.DoesNotContain("script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<strong>safe</strong>", result);
    }

    [Fact]
    public void SanitizerPreservesRasterInlineImagesAndRejectsSvgOrUnsafeSources()
    {
        var sanitizer = new CertificationEmailHtmlSanitizer();
        var result = sanitizer.Sanitize("<p><img src=\"data:image/png;base64,iVBORw0KGgo=\" alt=\"Logo\"></p><img src=\"data:image/svg+xml;base64,PHNjcmlwdD4=\"><img src=\"data:text/html;base64,SGk=\"><img src=\"javascript:alert(1)\"><a href=\"data:text/html;base64,SGk=\">unsafe</a>");

        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", result);
        Assert.Contains("alt=\"Logo\"", result);
        Assert.DoesNotContain("svg+xml", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"data:", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizerPreservesInlineTextColor()
    {
        var result = new CertificationEmailHtmlSanitizer().Sanitize("<p><span style=\"color: rgb(161, 0, 0);\">Von Scheffler</span></p>");

        Assert.Contains("color", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Von Scheffler", result);
    }

    [Fact]
    public void SanitizerPreservesLegacyFontColorFromPastedSignatures()
    {
        var result = new CertificationEmailHtmlSanitizer().Sanitize("<p><font color=\"#a10000\"><strong>Von Scheffler</strong></font></p>");

        Assert.Contains("a10000", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Von Scheffler", result);
    }

    [Fact]
    public void SanitizerPreservesOutlookInlineColorVariants()
    {
        var result = new CertificationEmailHtmlSanitizer().SanitizeForEmail("<span style=\"mso-style-text-fill-fill-color:#a10000;color:#a10000 !important\">Von Scheffler</span>");

        Assert.Contains("Von Scheffler", result);
        Assert.Contains("a10000", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmailSanitizerConvertsOpaqueRgbaColorsToOutlookCompatibleHex()
    {
        var result = new CertificationEmailHtmlSanitizer().SanitizeForEmail("<p><span style=\"color: rgb(161, 0, 0);\">Von Scheffler</span></p>");

        Assert.Contains("#A10000", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rgba(", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmailSanitizerResetsParagraphMarginsForDelivery()
    {
        var result = new CertificationEmailHtmlSanitizer().SanitizeForEmail("<p>First</p><p style=\"color: #a10000;\">Second</p>");

        Assert.Contains("<p style=\"margin:0; line-height:1.4;\">First</p>", result);
        Assert.Contains("margin:0; line-height:1.4;", result);
        Assert.Contains("color", result, StringComparison.OrdinalIgnoreCase);
    }

    private static CertificationEmailTemplateEditModel Template(CertificationEmailTemplateType type, string subject, string body) => new(type, subject, body, default, null);
    private static CertificationPackage Package(CertificationEmailTemplateType shape) => new(1, "Acme", 2, "North", new DateOnly(2026, 9, 1), "package.pdf", [], shape switch
    {
        CertificationEmailTemplateType.SingleLot => [new CertificationPackageLot(1, 10, "A", "Part 1", new DateOnly(2026, 8, 28), [], [])],
        CertificationEmailTemplateType.SinglePartMultiLot => [new CertificationPackageLot(1, 10, "A", "Part 1", new DateOnly(2026, 8, 28), [], []), new CertificationPackageLot(2, 10, "B", "Part 1", new DateOnly(2026, 8, 28), [], [])],
        _ => [new CertificationPackageLot(1, 10, "A", "Part 1", new DateOnly(2026, 8, 28), [], []), new CertificationPackageLot(2, 11, "B", "Part 2", new DateOnly(2026, 8, 28), [], [])]
    }, ["to@example.com"], []);
}
