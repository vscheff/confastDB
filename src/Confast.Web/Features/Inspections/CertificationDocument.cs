namespace Confast.Web.Features.Inspections;

public sealed class CertificationDocument
{
    public long Id { get; set; }

    public long InspectionCertificationId { get; set; }

    public InspectionCertification InspectionCertification { get; set; } = null!;

    public string OriginalFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];

    // This is a separately generated, rasterized PDF used only by the embedded viewer.
    // The uploaded original in Content is never replaced or rewritten.
    public byte[]? PreviewContent { get; set; }

    public DateTimeOffset UploadedAtUtc { get; set; }

    public uint Version { get; set; }
}
