using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Confast.Web.Features.Inspections;

public sealed class PdfDocumentMerger
{
    public byte[] Merge(byte[] inspectionSheet, IEnumerable<byte[]> certificationDocuments)
    {
        ArgumentNullException.ThrowIfNull(inspectionSheet);
        ArgumentNullException.ThrowIfNull(certificationDocuments);

        var documents = new List<PdfDocument>();
        var streams = new List<MemoryStream>();

        try
        {
            using var output = new PdfDocument();
            Append(output, inspectionSheet, documents, streams);

            foreach (var certificationDocument in certificationDocuments)
            {
                Append(output, certificationDocument, documents, streams);
            }

            using var result = new MemoryStream();
            output.Save(result, closeStream: false);
            return result.ToArray();
        }
        finally
        {
            foreach (var document in documents)
            {
                document.Dispose();
            }

            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    private static void Append(
        PdfDocument output,
        byte[] content,
        ICollection<PdfDocument> documents,
        ICollection<MemoryStream> streams)
    {
        var stream = new MemoryStream(content, writable: false);
        streams.Add(stream);

        var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        documents.Add(document);

        foreach (var page in document.Pages)
        {
            output.AddPage(page);
        }
    }
}
