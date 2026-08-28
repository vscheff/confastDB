using Confast.Web.Features.Inspections;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Confast.Web.Tests;

public sealed class PdfDocumentMergerTests
{
    [Fact]
    public void MergeAppendsEveryCertificationInOrder()
    {
        var inspectionSheet = CreatePdf(1);
        var firstCertification = CreatePdf(2);
        var secondCertification = CreatePdf(3);

        var result = new PdfDocumentMerger().Merge(
            inspectionSheet,
            [firstCertification, secondCertification]);

        using var document = PdfReader.Open(
            new MemoryStream(result, writable: false),
            PdfDocumentOpenMode.Import);
        Assert.Equal(6, document.PageCount);
    }

    [Fact]
    public void MergeReturnsInspectionSheetWhenThereAreNoCertifications()
    {
        var result = new PdfDocumentMerger().Merge(CreatePdf(2), []);

        using var document = PdfReader.Open(
            new MemoryStream(result, writable: false),
            PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.PageCount);
    }

    private static byte[] CreatePdf(int pageCount)
    {
        using var document = new PdfDocument();
        for (var index = 0; index < pageCount; index++)
        {
            document.AddPage();
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }
}
