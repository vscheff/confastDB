using Confast.Web.Features.Customers;

namespace Confast.Web.Tests;

public sealed class CertificationPackageFilenameFormatterTests
{
    private readonly CertificationPackageFilenameFormatter formatter = new();
    private readonly CertificationPackageFilenameValues values = new(
        "Acme Fasteners",
        "ABC123",
        "856342",
        "PO-1001",
        new DateOnly(2026, 8, 28),
        new DateOnly(2026, 9, 1));

    [Fact]
    public void ReplacesEverySupportedTokenAndAddsPdfExtension()
    {
        var result = formatter.Format(
            "{CustomerName}_{PartNumber}_{LotNumber}_{PONumber}_{InspectionDate}_{ShipDate}",
            values);

        Assert.Equal("Acme Fasteners_ABC123_856342_PO-1001_2026-08-28_090126.pdf", result);
    }

    [Fact]
    public void UsesCentralDefaultWhenCustomerTemplateIsMissing()
    {
        Assert.Equal(
            "ABC123_856342.pdf",
            formatter.Format(null, values));
        Assert.Equal(
            "ABC123_856342.pdf",
            formatter.Format("   ", values));
    }

    [Fact]
    public void CustomerTemplateOverridesTheSystemDefaultWithoutDuplicatingExtension()
    {
        Assert.Equal(
            "ABC123_856342_PACKAGE.pdf",
            formatter.Format("{PartNumber}_{LotNumber}_PACKAGE.PDF", values));
    }

    [Fact]
    public void RejectsUnknownTokens()
    {
        var exception = Assert.Throws<CertificationFilenameTemplateException>(
            () => formatter.Format("{PartNumber}_{CertificationType}", values));

        Assert.Contains("{CertificationType}", exception.Message);
    }

    [Fact]
    public void RejectsMalformedTokens()
    {
        Assert.Throws<CertificationFilenameTemplateException>(
            () => formatter.Format("{PartNumber", values));
    }

    [Fact]
    public void SanitizesInvalidWindowsFilenameCharactersInValuesAndLiterals()
    {
        var unsafeValues = values with
        {
            CustomerName = "ACME: East/West",
            PartNumber = "A*12?"
        };

        Assert.Equal(
            "ACME_ East_West_A_12_.pdf",
            formatter.Format("{CustomerName}/{PartNumber}", unsafeValues));
    }

    [Fact]
    public void MultiLotFilenameUsesItsOwnCustomerOnlyTokenLanguageAndDefault()
    {
        var multiLotValues = new CertificationMultiLotPackageFilenameValues(
            "Acme Fasteners",
            new DateOnly(2026, 9, 1));

        Assert.Equal(
            "Acme Fasteners.pdf",
            formatter.FormatMultiLot(null, multiLotValues));
        Assert.Equal(
            "Acme Fasteners_090126_COMBINED_CERTS.pdf",
            formatter.FormatMultiLot("{CustomerName}_{ShipDate}_COMBINED_CERTS", multiLotValues));
        Assert.Contains("{ShipDate}", CertificationPackageFilenameFormatter.SupportedTokens);
        Assert.Contains("{ShipDate}", CertificationPackageFilenameFormatter.MultiLotSupportedTokens);
    }

    [Fact]
    public void MultiLotFilenameRejectsSingleLotTokens()
    {
        var exception = Assert.Throws<CertificationFilenameTemplateException>(() =>
            formatter.FormatMultiLot(
                "{CustomerName}_{LotNumber}",
                new CertificationMultiLotPackageFilenameValues("Acme Fasteners")));

        Assert.Contains("{LotNumber}", exception.Message);
    }

    [Fact]
    public void MultiLotFilenameSanitizesInvalidCustomerNameCharacters()
    {
        Assert.Equal(
            "ACME_ East_West_CERTS.pdf",
            formatter.FormatMultiLot(
                "{CustomerName}_CERTS",
                new CertificationMultiLotPackageFilenameValues("ACME: East/West")));
    }
}
